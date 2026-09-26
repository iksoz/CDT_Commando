using Commando.Shared;

namespace Commando.Server;

public sealed class CommandoOptions
{
    public string CompetitionName { get; set; } = "Commando Local Range";
    public string DataDirectory { get; set; } = "data";
    public int TaskLifetimeMinutes { get; set; } = 10;
    public int MaxOutputBytes { get; set; } = 262_144;
}

public sealed class StoredAgent
{
    public Guid Id { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string OsDescription { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string OsFamily { get; set; } = string.Empty;
    public string HostPolicyName { get; set; } = string.Empty;
    public List<string> AllowedServiceIds { get; set; } = [];
    public string RemoteAddress { get; set; } = string.Empty;
    public DateTimeOffset RegisteredUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public string Status { get; set; } = "active";

    public AgentSummary ToSummary() => new(
        Id, Name, Hostname, UserName, OsDescription, Architecture, RemoteAddress,
        RegisteredUtc, LastSeenUtc, Status, OsFamily, AllowedServiceIds);
}

public sealed class StoredTask
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string ParametersJson { get; set; } = "{}";
    public string Status { get; set; } = "queued";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public bool? Success { get; set; }
    public string? OutputJson { get; set; }
    public string? Error { get; set; }

    public TaskSummary ToSummary() => new(
        Id, AgentId, Kind, ParametersJson, Status, CreatedUtc, ExpiresUtc,
        StartedUtc, CompletedUtc, Success, OutputJson, Error);
}

public sealed class PersistedState
{
    public List<StoredAgent> Agents { get; set; } = [];
    public List<StoredTask> Tasks { get; set; } = [];
    public List<AuditEntry> Audit { get; set; } = [];
    public bool EmergencyStopped { get; set; }
    public DateTimeOffset? EmergencyStoppedUtc { get; set; }
    public bool Paused { get; set; }
    public DateTimeOffset? PausedUtc { get; set; }
}
