using System.Text.Json;
using System.Net;
using Commando.Shared;

namespace Commando.Server;

public sealed class StateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _statePath;
    private readonly CompetitionPolicy _policy;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private PersistedState _state;

    public StateStore(IWebHostEnvironment environment, CommandoOptions options, CompetitionPolicy policy)
    {
        _policy = policy;
        var directory = Path.IsPathRooted(options.DataDirectory)
            ? options.DataDirectory
            : Path.Combine(environment.ContentRootPath, options.DataDirectory);
        Directory.CreateDirectory(directory);
        _statePath = Path.Combine(directory, "commando-state.json");
        _state = Load();
    }

    public async Task<(StoredAgent Agent, string Token)?> RegisterAgentAsync(
        AgentRegistrationRequest request, CompetitionHost host, string remoteAddress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_policy.CurrentState(_state.Paused, _state.EmergencyStopped, DateTimeOffset.UtcNow) is "ended" or "emergency_stopped")
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var token = Security.NewToken();
            var agent = new StoredAgent
            {
                Id = Guid.NewGuid(),
                TokenHash = Security.HashToken(token),
                Name = host.Name,
                Hostname = host.Hostname,
                UserName = Limit(request.UserName, 255),
                OsDescription = Limit(request.OsDescription, 500),
                OsFamily = host.OsFamily,
                HostPolicyName = host.Name,
                AllowedServiceIds = host.AllowedServiceIds.ToList(),
                Architecture = Limit(request.Architecture, 50),
                AgentVersion = Limit(request.AgentVersion, 50),
                RemoteAddress = Limit(remoteAddress, 100),
                RegisteredUtc = now,
                LastSeenUtc = now
            };
            _state.Agents.Add(agent);
            AddAudit("agent", "registered", agent.Id.ToString("D"), $"{agent.Name} on {agent.Hostname}");
            await SaveAsync(cancellationToken);
            return (agent, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsEmergencyStoppedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _state.EmergencyStopped;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EventControlResponse> GetControlAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            return new EventControlResponse(
                _policy.CurrentState(_state.Paused, _state.EmergencyStopped, now),
                _policy.StartsUtc, _policy.ExpiresUtc, now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EventControlResponse?> SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_policy.CurrentState(false, _state.EmergencyStopped, now) is "ended" or "emergency_stopped")
                return null;

            _state.Paused = paused;
            _state.PausedUtc = paused ? now : null;
            if (paused)
            {
                foreach (var task in _state.Tasks.Where(item => item.Status == "queued" && item.Kind != TaskKinds.StopAgent))
                {
                    task.Status = "cancelled";
                    task.CompletedUtc = now;
                    task.Error = "Cancelled by event pause.";
                }
            }
            AddAudit("operator", paused ? "pause" : "resume", "event", "Operator changed the event state.");
            await SaveAsync(cancellationToken);
            return new EventControlResponse(
                _policy.CurrentState(_state.Paused, _state.EmergencyStopped, now),
                _policy.StartsUtc, _policy.ExpiresUtc, now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<object> EmergencyStopAsync(DateTimeOffset taskExpiresUtc, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state.EmergencyStopped)
            {
                return new
                {
                    emergencyStopped = true,
                    stoppedUtc = _state.EmergencyStoppedUtc,
                    agentsTargeted = 0,
                    alreadyStopped = true
                };
            }

            var now = DateTimeOffset.UtcNow;
            _state.EmergencyStopped = true;
            _state.EmergencyStoppedUtc = now;
            foreach (var task in _state.Tasks.Where(item => item.Status == "queued"))
            {
                task.Status = "cancelled";
                task.CompletedUtc = now;
                task.Error = "Cancelled by the emergency stop.";
            }

            var activeAgents = _state.Agents.Where(item => item.Status == "active").ToList();
            foreach (var agent in activeAgents)
            {
                _state.Tasks.Add(new StoredTask
                {
                    Id = Guid.NewGuid(),
                    AgentId = agent.Id,
                    Kind = TaskKinds.StopAgent,
                    ParametersJson = "{}",
                    Status = "queued",
                    CreatedUtc = now,
                    ExpiresUtc = taskExpiresUtc
                });
            }

            AddAudit("operator", "emergency_stop", "all_agents", $"agents_targeted={activeAgents.Count}");
            await SaveAsync(cancellationToken);
            return new
            {
                emergencyStopped = true,
                stoppedUtc = now,
                agentsTargeted = activeAgents.Count,
                alreadyStopped = false
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredAgent?> AuthenticateAgentAsync(Guid agentId, string token, IPAddress? remoteAddress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var agent = _state.Agents.SingleOrDefault(item => item.Id == agentId);
            var host = agent is null ? null : _policy.FindHost(agent.HostPolicyName);
            if (agent is null || host is null || !Security.TokenMatches(token, agent.TokenHash) ||
                _policy.RequireSourceIp &&
                (!IPAddress.TryParse(host.Address, out var expected) ||
                 !expected.Equals(remoteAddress?.MapToIPv4())))
            {
                return null;
            }

            agent.LastSeenUtc = DateTimeOffset.UtcNow;
            await SaveAsync(cancellationToken);
            return agent;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _state.Agents.OrderByDescending(item => item.LastSeenUtc).Select(item => item.ToSummary()).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredTask?> QueueTaskAsync(
        Guid agentId, QueueTaskRequest request, DateTimeOffset expiresUtc, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var currentState = _policy.CurrentState(_state.Paused, _state.EmergencyStopped, DateTimeOffset.UtcNow);
            if (currentState is "ended" or "emergency_stopped" ||
                currentState != "live" && request.Kind != TaskKinds.StopAgent)
            {
                return null;
            }

            var agent = _state.Agents.SingleOrDefault(item => item.Id == agentId && item.Status == "active");
            if (agent is null)
            {
                return null;
            }

            var host = _policy.FindHost(agent.HostPolicyName);
            if (host is null || (request.Kind == TaskKinds.ServiceStatus || request.Kind == TaskKinds.ServicePause) &&
                !ServiceTaskParameters.IsAllowed(request.Parameters, host.AllowedServiceIds, request.Kind))
                return null;

            var task = new StoredTask
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                Kind = request.Kind.ToLowerInvariant(),
                ParametersJson = request.Parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? "{}"
                    : request.Parameters.GetRawText(),
                CreatedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = expiresUtc
            };
            _state.Tasks.Add(task);
            AddAudit("operator", "task_queued", task.Id.ToString("D"), $"{task.Kind} for {agentId:D}");
            await SaveAsync(cancellationToken);
            return task;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredTask?> TakeNextTaskAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in _state.Tasks.Where(item => item.AgentId == agentId && item.Status == "queued" && item.ExpiresUtc <= now))
            {
                expired.Status = "expired";
            }

            var task = _state.Tasks
                .Where(item => item.AgentId == agentId && item.Status == "queued" && item.ExpiresUtc > now &&
                    (item.Kind == TaskKinds.StopAgent || _policy.CurrentState(_state.Paused, _state.EmergencyStopped, now) == "live"))
                .OrderBy(item => item.CreatedUtc)
                .FirstOrDefault();
            if (task is null)
            {
                await SaveAsync(cancellationToken);
                return null;
            }

            task.Status = "dispatched";
            task.StartedUtc = now;
            AddAudit("agent", "task_dispatched", task.Id.ToString("D"), task.Kind);
            await SaveAsync(cancellationToken);
            return task;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> CompleteTaskAsync(
        Guid agentId, Guid taskId, TaskResultRequest result, int maxOutputBytes, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var task = _state.Tasks.SingleOrDefault(item => item.Id == taskId && item.AgentId == agentId);
            if (task is null || task.Status is "completed" or "cancelled")
            {
                return false;
            }

            task.Status = "completed";
            task.StartedUtc = result.StartedUtc;
            task.CompletedUtc = result.CompletedUtc;
            task.Success = result.Success;
            task.OutputJson = LimitUtf8(result.OutputJson, maxOutputBytes);
            task.Error = result.Error is null ? null : Limit(result.Error, 4000);
            if (task.Kind.Equals(TaskKinds.StopAgent, StringComparison.OrdinalIgnoreCase))
            {
                var agent = _state.Agents.SingleOrDefault(item => item.Id == agentId);
                if (agent is not null)
                {
                    agent.Status = "stopped";
                }
            }
            AddAudit("agent", "task_completed", task.Id.ToString("D"), $"success={result.Success}");
            await SaveAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TaskSummary>> GetTasksAsync(Guid? agentId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _state.Tasks
                .Where(item => agentId is null || item.AgentId == agentId)
                .OrderByDescending(item => item.CreatedUtc)
                .Take(500)
                .Select(item => item.ToSummary())
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _state.Audit.OrderByDescending(item => item.TimestampUtc).Take(1000).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    private PersistedState Load()
    {
        if (!File.Exists(_statePath))
        {
            return new PersistedState();
        }

        try
        {
            return JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_statePath), _json) ?? new PersistedState();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Could not parse state file '{_statePath}'.", exception);
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var temporaryPath = _statePath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(_state, _json), cancellationToken);
        File.Move(temporaryPath, _statePath, true);
    }

    private void AddAudit(string actor, string action, string target, string detail)
    {
        _state.Audit.Add(new AuditEntry(DateTimeOffset.UtcNow, actor, action, target, detail));
        if (_state.Audit.Count > 10_000)
        {
            _state.Audit.RemoveRange(0, _state.Audit.Count - 10_000);
        }
    }

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string LimitUtf8(string value, int maxBytes)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        return System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(value).AsSpan(0, maxBytes));
    }
}
