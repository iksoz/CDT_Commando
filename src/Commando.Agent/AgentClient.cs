using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Commando.Shared;

namespace Commando.Agent;

public sealed class AgentClient : IDisposable
{
    private readonly AgentConfig _config;
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private AgentState? _state;

    public AgentClient(AgentConfig config)
    {
        _config = config;
        _http = new HttpClient
        {
            BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(10, config.TaskTimeoutSeconds + 5))
        };
        _state = LoadState();
    }

    public AgentState State => _state ?? throw new InvalidOperationException("The agent has not enrolled.");

    public async Task EnrollAsync(CancellationToken cancellationToken)
    {
        if (_state is not null)
        {
            return;
        }

        var request = new AgentRegistrationRequest(
            _config.AgentName,
            Environment.MachineName,
            Environment.UserName,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            typeof(AgentClient).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Unsupported");
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/agent/register")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("X-Enrollment-Key", _config.EnrollmentKey);
        using var response = await _http.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, "enrollment", cancellationToken);
        _state = await response.Content.ReadFromJsonAsync<AgentState>(_json, cancellationToken)
            ?? throw new InvalidOperationException("The server returned an empty enrollment response.");
        await SaveStateAsync(cancellationToken);
    }

    public async Task<TaskEnvelope?> GetNextTaskAsync(CancellationToken cancellationToken)
    {
        using var request = CreateAgentRequest(HttpMethod.Get, $"api/agent/{State.AgentId:D}/tasks/next");
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Gone)
        {
            throw new CompetitionExpiredException();
        }

        await EnsureSuccessAsync(response, "task polling", cancellationToken);
        return await response.Content.ReadFromJsonAsync<TaskEnvelope>(_json, cancellationToken)
            ?? throw new InvalidOperationException("The server returned an empty task response.");
    }

    public async Task<EventControlResponse> GetControlAsync(CancellationToken cancellationToken)
    {
        using var request = CreateAgentRequest(HttpMethod.Get, $"api/agent/{State.AgentId:D}/control");
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "control check", cancellationToken);
        return await response.Content.ReadFromJsonAsync<EventControlResponse>(_json, cancellationToken)
            ?? throw new InvalidOperationException("The server returned an empty control response.");
    }

    public async Task SubmitResultAsync(Guid taskId, TaskResultRequest result, CancellationToken cancellationToken)
    {
        using var request = CreateAgentRequest(
            HttpMethod.Post,
            $"api/agent/{State.AgentId:D}/tasks/{taskId:D}/result",
            JsonContent.Create(result));
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "result submission", cancellationToken);
    }

    private HttpRequestMessage CreateAgentRequest(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-Agent-Token", State.AgentToken);
        return request;
    }

    private AgentState? LoadState()
    {
        if (!File.Exists(_config.StatePath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<AgentState>(File.ReadAllText(_config.StatePath), _json)
            ?? throw new InvalidOperationException("Agent state is empty.");
    }

    private async Task SaveStateAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_config.StatePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(_config.StatePath, JsonSerializer.Serialize(State, _json), cancellationToken);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"Agent {operation} failed with {(int)response.StatusCode}: {body}");
    }

    public void Dispose() => _http.Dispose();
}

public sealed class CompetitionExpiredException : Exception;
