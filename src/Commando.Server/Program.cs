using System.Text.Json;
using Microsoft.Extensions.Options;
using Commando.Server;
using Commando.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<CommandoOptions>(builder.Configuration.GetSection("Commando"));
builder.Services.AddSingleton(serviceProvider => serviceProvider.GetRequiredService<IOptions<CommandoOptions>>().Value);
builder.Services.AddSingleton(CompetitionPolicy.Load(
    Environment.GetEnvironmentVariable("COMMANDO_POLICY_FILE"), builder.Environment.ContentRootPath));
builder.Services.AddSingleton<StateStore>();

var operatorKey = Environment.GetEnvironmentVariable("COMMANDO_OPERATOR_KEY") ?? string.Empty;
var enrollmentKey = Environment.GetEnvironmentVariable("COMMANDO_ENROLLMENT_KEY") ?? string.Empty;
if (operatorKey.Length < 16 || enrollmentKey.Length < 16)
{
    throw new InvalidOperationException(
        "Set COMMANDO_OPERATOR_KEY and COMMANDO_ENROLLMENT_KEY to separate values of at least 16 characters.");
}

var operatorKeyHash = Security.HashToken(operatorKey);
var app = builder.Build();
var options = app.Services.GetRequiredService<CommandoOptions>();
var policy = app.Services.GetRequiredService<CompetitionPolicy>();

app.UseDefaultFiles();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/operator"))
    {
        var supplied = context.Request.Headers["X-Operator-Key"].ToString();
        if (!Security.TokenMatches(supplied, operatorKeyHash))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid operator key." });
            return;
        }
    }

    await next();
});

app.MapGet("/api/status", async (StateStore store, CancellationToken cancellationToken) => Results.Ok(new
{
    name = options.CompetitionName,
    control = await store.GetControlAsync(cancellationToken),
    taskKinds = TaskKinds.Allowed.Order(StringComparer.Ordinal).ToArray(),
    powerShellCommands = PowerShellCommands.Allowed.Order(StringComparer.Ordinal).ToArray(),
    mode = "competition-scoped"
}));

app.MapPost("/api/agent/register", async (
    HttpContext context,
    AgentRegistrationRequest request,
    StateStore store,
    CancellationToken cancellationToken) =>
{
    if (DateTimeOffset.UtcNow >= policy.ExpiresUtc)
    {
        return Results.StatusCode(StatusCodes.Status410Gone);
    }

    if (await store.IsEmergencyStoppedAsync(cancellationToken))
    {
        return Results.StatusCode(StatusCodes.Status423Locked);
    }

    if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Hostname))
    {
        return Results.BadRequest(new { error = "Agent name and hostname are required." });
    }

    var host = policy.MatchRegistration(request, context.Connection.RemoteIpAddress);
    if (host is null)
        return Results.BadRequest(new { error = "Agent host, OS, or source address is outside the event policy." });

    var supplied = context.Request.Headers["X-Enrollment-Key"].ToString();
    var expectedEnrollmentKey = Security.DeriveHostEnrollmentKey(enrollmentKey, host.Name);
    if (!Security.TokenMatches(supplied, Security.HashToken(expectedEnrollmentKey)))
        return Results.Unauthorized();

    var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var registration = await store.RegisterAgentAsync(request, host, remoteAddress, cancellationToken);
    if (registration is null)
    {
        return Results.StatusCode(StatusCodes.Status423Locked);
    }
    var (agent, token) = registration.Value;
    return Results.Ok(new AgentRegistrationResponse(agent.Id, token, policy.ExpiresUtc));
});

app.MapGet("/api/agent/{agentId:guid}/control", async (
    Guid agentId, HttpContext context, StateStore store, CancellationToken cancellationToken) =>
{
    var authentication = await AuthenticateAgent(context, store, agentId, cancellationToken);
    return authentication is null ? Results.Unauthorized() : Results.Ok(await store.GetControlAsync(cancellationToken));
});

app.MapGet("/api/agent/{agentId:guid}/tasks/next", async (
    Guid agentId,
    HttpContext context,
    StateStore store,
    CancellationToken cancellationToken) =>
{
    var authentication = await AuthenticateAgent(context, store, agentId, cancellationToken);
    if (authentication is null)
    {
        return Results.Unauthorized();
    }

    if (DateTimeOffset.UtcNow >= policy.ExpiresUtc)
    {
        return Results.StatusCode(StatusCodes.Status410Gone);
    }

    var task = await store.TakeNextTaskAsync(agentId, cancellationToken);
    if (task is null)
    {
        return Results.NoContent();
    }

    var unsigned = new TaskEnvelope(
        task.Id, task.AgentId, task.Kind, task.ParametersJson,
        task.CreatedUtc, task.ExpiresUtc, string.Empty);
    return Results.Ok(unsigned with { Signature = TaskSigning.Sign(unsigned, authentication.Value.Token) });
});

app.MapPost("/api/agent/{agentId:guid}/tasks/{taskId:guid}/result", async (
    Guid agentId,
    Guid taskId,
    HttpContext context,
    TaskResultRequest result,
    StateStore store,
    CancellationToken cancellationToken) =>
{
    var authentication = await AuthenticateAgent(context, store, agentId, cancellationToken);
    if (authentication is null)
    {
        return Results.Unauthorized();
    }

    var completed = await store.CompleteTaskAsync(
        agentId, taskId, result, options.MaxOutputBytes, cancellationToken);
    return completed ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/api/operator/agents", async (StateStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.GetAgentsAsync(cancellationToken)));

app.MapGet("/api/operator/tasks", async (
    Guid? agentId, StateStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.GetTasksAsync(agentId, cancellationToken)));

app.MapGet("/api/operator/audit", async (StateStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.GetAuditAsync(cancellationToken)));

app.MapPost("/api/operator/emergency-stop", async (
    JsonElement request,
    StateStore store,
    CancellationToken cancellationToken) =>
{
    if (request.ValueKind != JsonValueKind.Object ||
        !request.TryGetProperty("confirmation", out var confirmation) ||
        confirmation.GetString() != "STOP COMMANDO")
    {
        return Results.BadRequest(new { error = "Emergency stop requires the exact confirmation 'STOP COMMANDO'." });
    }

    var result = await store.EmergencyStopAsync(DateTimeOffset.UtcNow.AddHours(1), cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/api/operator/pause", async (StateStore store, CancellationToken cancellationToken) =>
{
    var control = await store.SetPausedAsync(true, cancellationToken);
    return control is null ? Results.Conflict(new { error = "Event already ended or emergency-stopped." }) : Results.Ok(control);
});

app.MapPost("/api/operator/resume", async (StateStore store, CancellationToken cancellationToken) =>
{
    var control = await store.SetPausedAsync(false, cancellationToken);
    return control is null ? Results.Conflict(new { error = "Event already ended or emergency-stopped." }) : Results.Ok(control);
});

app.MapPost("/api/operator/agents/{agentId:guid}/tasks", async (
    Guid agentId,
    QueueTaskRequest request,
    StateStore store,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Kind))
        return Results.BadRequest(new { error = "Task kind is required." });
    request = request with { Kind = request.Kind.ToLowerInvariant() };
    if (DateTimeOffset.UtcNow >= policy.ExpiresUtc)
    {
        return Results.StatusCode(StatusCodes.Status410Gone);
    }

    if (await store.IsEmergencyStoppedAsync(cancellationToken))
    {
        return Results.StatusCode(StatusCodes.Status423Locked);
    }

    var control = await store.GetControlAsync(cancellationToken);
    if (control.State != "live" && request.Kind != TaskKinds.StopAgent)
        return Results.StatusCode(StatusCodes.Status423Locked);

    if (!TaskKinds.Allowed.Contains(request.Kind))
    {
        return Results.BadRequest(new { error = "Task kind is not allowlisted.", allowed = TaskKinds.Allowed });
    }

    if (!ValidateParameters(request, policy, out var parameterError))
    {
        return Results.BadRequest(new { error = parameterError });
    }

    var configuredLifetime = TimeSpan.FromMinutes(Math.Clamp(options.TaskLifetimeMinutes, 1, 60));
    var expiresUtc = new[] { DateTimeOffset.UtcNow.Add(configuredLifetime), policy.ExpiresUtc }.Min();
    var task = await store.QueueTaskAsync(agentId, request, expiresUtc, cancellationToken);
    return task is null ? Results.NotFound() : Results.Accepted(value: task.ToSummary());
});

app.MapFallbackToFile("index.html");
app.Run();

static async Task<(StoredAgent Agent, string Token)?> AuthenticateAgent(
    HttpContext context,
    StateStore store,
    Guid agentId,
    CancellationToken cancellationToken)
{
    var token = context.Request.Headers["X-Agent-Token"].ToString();
    var agent = await store.AuthenticateAgentAsync(agentId, token, context.Connection.RemoteIpAddress, cancellationToken);
    return agent is null ? null : (agent, token);
}

static bool ValidateParameters(QueueTaskRequest request, CompetitionPolicy policy, out string error)
{
    error = string.Empty;
    if (request.Kind.Equals(TaskKinds.RestrictedPowerShell, StringComparison.OrdinalIgnoreCase))
    {
        return ValidatePowerShellParameters(request.Parameters, policy, out error);
    }

    if (request.Kind is TaskKinds.ServiceStatus or TaskKinds.ServicePause)
        return ServiceTaskParameters.TryParse(request.Parameters, request.Kind, out _, out error);

    if (!request.Kind.Equals(TaskKinds.HashFile, StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (request.Parameters.ValueKind != JsonValueKind.Object ||
        !request.Parameters.TryGetProperty("path", out var path) ||
        path.ValueKind != JsonValueKind.String ||
        string.IsNullOrWhiteSpace(path.GetString()))
    {
        error = "hash_file requires a non-empty 'path' string.";
        return false;
    }

    return true;
}

static bool ValidatePowerShellParameters(JsonElement parameters, CompetitionPolicy policy, out string error)
{
    error = string.Empty;
    if (parameters.ValueKind != JsonValueKind.Object ||
        !parameters.TryGetProperty("command", out var commandElement) ||
        commandElement.ValueKind != JsonValueKind.String)
    {
        error = "powershell_readonly requires a command and an optional arguments object.";
        return false;
    }

    var command = commandElement.GetString() ?? string.Empty;
    if (!PowerShellCommands.Allowed.Contains(command))
    {
        error = $"PowerShell command is not allowlisted. Allowed: {string.Join(", ", PowerShellCommands.Allowed)}";
        return false;
    }

    foreach (var property in parameters.EnumerateObject())
    {
        if (property.Name is not ("command" or "arguments"))
        {
            error = $"Unexpected PowerShell request property '{property.Name}'.";
            return false;
        }
    }

    var arguments = parameters.TryGetProperty("arguments", out var suppliedArguments)
        ? suppliedArguments
        : default;
    if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
    {
        if (command.Equals(PowerShellCommands.TestConnection, StringComparison.OrdinalIgnoreCase))
        {
            error = "Test-Connection requires a target.";
            return false;
        }
        return true;
    }

    if (arguments.ValueKind != JsonValueKind.Object)
    {
        error = "PowerShell arguments must be an object.";
        return false;
    }

    var allowedArgumentNames = command switch
    {
        PowerShellCommands.GetProcess => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name" },
        PowerShellCommands.GetService => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name" },
        PowerShellCommands.GetHotFix => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id" },
        PowerShellCommands.GetNetTcpConnection => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "state" },
        PowerShellCommands.TestConnection => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "target", "count" },
        _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    };

    foreach (var argument in arguments.EnumerateObject())
    {
        if (!allowedArgumentNames.Contains(argument.Name))
        {
            error = $"Argument '{argument.Name}' is not allowed for {command}.";
            return false;
        }

        if (argument.Name.Equals("count", StringComparison.OrdinalIgnoreCase))
        {
            if (argument.Value.ValueKind != JsonValueKind.Number ||
                !argument.Value.TryGetInt32(out var count) || count is < 1 or > 4)
            {
                error = "Test-Connection count must be an integer from 1 to 4.";
                return false;
            }
        }
        else if (argument.Value.ValueKind != JsonValueKind.String || !IsSafeArgument(argument.Value.GetString()))
        {
            error = $"Argument '{argument.Name}' must be a non-empty string of at most 255 characters without control characters.";
            return false;
        }
    }

    if (command.Equals(PowerShellCommands.TestConnection, StringComparison.OrdinalIgnoreCase) &&
        (!arguments.TryGetProperty("target", out var target) || !IsSafeArgument(target.GetString())))
    {
        error = "Test-Connection requires a target. The agent applies its local target scope before execution.";
        return false;
    }

    if (command.Equals(PowerShellCommands.TestConnection, StringComparison.OrdinalIgnoreCase) &&
        !policy.IsAllowedTarget(arguments.GetProperty("target").GetString()!))
    {
        error = "Test-Connection target must be a literal IP of a listed event host.";
        return false;
    }

    return true;
}

static bool IsSafeArgument(string? value) =>
    !string.IsNullOrWhiteSpace(value) && value.Length <= 255 && !value.Any(char.IsControl);

public partial class Program;
