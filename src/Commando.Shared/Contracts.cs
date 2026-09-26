using System.Text.Json;

namespace Commando.Shared;

public static class TaskKinds
{
    public const string SystemInfo = "system_info";
    public const string ListProcesses = "list_processes";
    public const string ListServices = "list_services";
    public const string NetworkConnections = "network_connections";
    public const string HashFile = "hash_file";
    public const string RestrictedPowerShell = "powershell_readonly";
    public const string PowerShellScript = "powershell_script";
    public const string ServiceStatus = "service_status";
    public const string ServicePause = "service_pause";
    public const string StopAgent = "stop_agent";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        SystemInfo,
        ListProcesses,
        ListServices,
        NetworkConnections,
        HashFile,
        RestrictedPowerShell,
        PowerShellScript,
        ServiceStatus,
        ServicePause,
        StopAgent
    };
}

public static class PowerShellCommands
{
    public const string GetComputerInfo = "Get-ComputerInfo";
    public const string GetProcess = "Get-Process";
    public const string GetService = "Get-Service";
    public const string GetHotFix = "Get-HotFix";
    public const string GetNetTcpConnection = "Get-NetTCPConnection";
    public const string TestConnection = "Test-Connection";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        GetComputerInfo,
        GetProcess,
        GetService,
        GetHotFix,
        GetNetTcpConnection,
        TestConnection
    };
}

public sealed record AgentRegistrationRequest(
    string Name,
    string Hostname,
    string UserName,
    string OsDescription,
    string Architecture,
    string AgentVersion,
    string OsFamily);

public sealed record AgentRegistrationResponse(
    Guid AgentId,
    string AgentToken,
    DateTimeOffset CompetitionExpiresUtc);

public sealed record EventControlResponse(
    string State,
    DateTimeOffset StartsUtc,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset ServerTimeUtc);

public sealed record AgentSummary(
    Guid Id,
    string Name,
    string Hostname,
    string UserName,
    string OsDescription,
    string Architecture,
    string RemoteAddress,
    DateTimeOffset RegisteredUtc,
    DateTimeOffset LastSeenUtc,
    string Status,
    string OsFamily,
    IReadOnlyList<string> AllowedServiceIds,
    bool AllowUnrestrictedPowerShell);

public sealed record QueueTaskRequest(string Kind, JsonElement Parameters);

public sealed record TaskEnvelope(
    Guid Id,
    Guid AgentId,
    string Kind,
    string ParametersJson,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    string Signature);

public sealed record TaskResultRequest(
    bool Success,
    string OutputJson,
    string? Error,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc);

public sealed record TaskSummary(
    Guid Id,
    Guid AgentId,
    string Kind,
    string ParametersJson,
    string Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    bool? Success,
    string? OutputJson,
    string? Error);

public sealed record AuditEntry(
    DateTimeOffset TimestampUtc,
    string Actor,
    string Action,
    string Target,
    string Detail);
