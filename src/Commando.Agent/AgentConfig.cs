using System.Text.Json;
using System.Text.RegularExpressions;
using Commando.Shared;

namespace Commando.Agent;

public sealed class AgentConfig
{
    public string ServerUrl { get; set; } = string.Empty;
    public string EnrollmentKey { get; set; } = string.Empty;
    public string AgentName { get; set; } = Environment.MachineName;
    public int PollSeconds { get; set; } = 5;
    public int TaskTimeoutSeconds { get; set; } = 30;
    public bool AllowInsecureLoopback { get; set; }
    public string StatePath { get; set; } = "commando-agent-state.json";
    public string RecoveryPath { get; set; } = "commando-agent-recovery.json";
    public List<string> AllowedHashRoots { get; set; } = [];
    public string TranscriptDirectory { get; set; } = "transcripts";
    public string PowerShellExecutable { get; set; } = "powershell.exe";
    public int MaxPowerShellOutputBytes { get; set; } = 131_072;
    public List<string> EnabledPowerShellCommands { get; set; } = [];
    public bool AllowUnrestrictedPowerShell { get; set; }
    public List<string> AllowedTargets { get; set; } = [];
    public List<string> AllowedCidrs { get; set; } = [];
    public List<LocalService> Services { get; set; } = [];

    public static AgentConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Agent configuration was not found.", path);
        }

        var config = JsonSerializer.Deserialize<AgentConfig>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Agent configuration is empty.");
        config.Validate(path);
        return config;
    }

    private void Validate(string configPath)
    {
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("ServerUrl must be an absolute URL.");
        }

        var isLoopback = uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !(AllowInsecureLoopback && isLoopback))
        {
            throw new InvalidOperationException("HTTPS is required. HTTP is permitted only for loopback testing when AllowInsecureLoopback is true.");
        }

        if (EnrollmentKey.Length < 16)
        {
            throw new InvalidOperationException("EnrollmentKey must contain at least 16 characters.");
        }

        PollSeconds = Math.Clamp(PollSeconds, 2, 60);
        TaskTimeoutSeconds = Math.Clamp(TaskTimeoutSeconds, 5, 120);
        MaxPowerShellOutputBytes = Math.Clamp(MaxPowerShellOutputBytes, 4096, 262_144);
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? AppContext.BaseDirectory;
        StatePath = Path.IsPathRooted(StatePath) ? StatePath : Path.Combine(configDirectory, StatePath);
        RecoveryPath = Path.IsPathRooted(RecoveryPath) ? RecoveryPath : Path.Combine(configDirectory, RecoveryPath);
        TranscriptDirectory = Path.IsPathRooted(TranscriptDirectory)
            ? Path.GetFullPath(TranscriptDirectory)
            : Path.GetFullPath(Path.Combine(configDirectory, TranscriptDirectory));
        AllowedHashRoots = AllowedHashRoots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        AllowedTargets = AllowedTargets
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().TrimEnd('.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        AllowedCidrs = AllowedCidrs
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var invalidCommand = EnabledPowerShellCommands.FirstOrDefault(command => !PowerShellCommands.Allowed.Contains(command));
        if (invalidCommand is not null)
        {
            throw new InvalidOperationException($"PowerShell command '{invalidCommand}' is not supported by the compiled allowlist.");
        }
        EnabledPowerShellCommands = EnabledPowerShellCommands.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var invalidCidr = AllowedCidrs.FirstOrDefault(cidr => !ScopePolicy.IsValidCidr(cidr));
        if (invalidCidr is not null)
        {
            throw new InvalidOperationException($"Allowed CIDR '{invalidCidr}' is invalid.");
        }

        if (OperatingSystem.IsWindows() && !Path.IsPathRooted(PowerShellExecutable))
        {
            if (!PowerShellExecutable.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("PowerShellExecutable must be an absolute path or the built-in powershell.exe.");
            }

            PowerShellExecutable = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
        }
        if (OperatingSystem.IsWindows())
        {
            PowerShellExecutable = Path.GetFullPath(PowerShellExecutable);
            var executableName = Path.GetFileName(PowerShellExecutable);
            if ((!executableName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) &&
                 !executableName.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)) ||
                !File.Exists(PowerShellExecutable))
                throw new InvalidOperationException("PowerShellExecutable must point to an existing powershell.exe or pwsh.exe.");
        }

        var serviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in Services)
        {
            if (!Regex.IsMatch(service.Id, "^[a-zA-Z][a-zA-Z0-9_-]{0,39}$") ||
                !Regex.IsMatch(service.ServiceName, "^[a-zA-Z0-9][a-zA-Z0-9._@-]{0,127}$") ||
                service.TcpPort is < 0 or > 65535 || !serviceIds.Add(service.Id))
                throw new InvalidOperationException($"Invalid or duplicate local service '{service.Id}'.");
        }
    }
}

public sealed record AgentState(Guid AgentId, string AgentToken, DateTimeOffset CompetitionExpiresUtc);

public sealed class LocalService
{
    public string Id { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public int TcpPort { get; set; }
    public bool AllowPause { get; set; }
}
