using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Commando.Shared;

namespace Commando.Agent;

public sealed record RestrictedPowerShellOutput(
    string Command,
    int ExitCode,
    bool TimedOut,
    string StandardOutput,
    string StandardError,
    string TranscriptPath,
    string TranscriptSha256);

public sealed record PowerShellScriptOutput(
    int ExitCode,
    bool TimedOut,
    string StandardOutput,
    string StandardError,
    bool OutputTruncated,
    string ScriptSha256,
    string TranscriptPath,
    string TranscriptSha256);

public sealed class RestrictedPowerShellExecutor
{
    private readonly AgentConfig _config;
    private readonly ScopePolicy _scope;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public RestrictedPowerShellExecutor(AgentConfig config)
    {
        _config = config;
        _scope = new ScopePolicy(config.AllowedTargets, config.AllowedCidrs);
    }

    public async Task<RestrictedPowerShellOutput> ExecuteAsync(
        Guid taskId,
        string parametersJson,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Restricted PowerShell tasks require Windows.");
        }

        var request = ParseAndValidate(parametersJson);
        var requestJson = JsonSerializer.Serialize(request, _json);
        var requestBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(requestJson));
        var script = BuildStaticScript(requestBase64);
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = _config.PowerShellExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedScript);

        var startedUtc = DateTimeOffset.UtcNow;
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("PowerShell could not be started.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process already exited between cancellation and termination.
            }
            await process.WaitForExitAsync(CancellationToken.None);
        }

        var stdout = LimitUtf8(await stdoutTask, _config.MaxPowerShellOutputBytes);
        var stderr = LimitUtf8(await stderrTask, _config.MaxPowerShellOutputBytes);
        var completedUtc = DateTimeOffset.UtcNow;
        var exitCode = timedOut ? -1 : process.ExitCode;

        Directory.CreateDirectory(_config.TranscriptDirectory);
        var transcriptPath = Path.Combine(
            _config.TranscriptDirectory,
            $"{startedUtc:yyyyMMdd-HHmmss}-{taskId:D}.json");
        var transcript = new
        {
            TaskId = taskId,
            Agent = Environment.MachineName,
            request.Command,
            request.Arguments,
            StartedUtc = startedUtc,
            CompletedUtc = completedUtc,
            DurationMilliseconds = (long)(completedUtc - startedUtc).TotalMilliseconds,
            TimedOut = timedOut,
            ExitCode = exitCode,
            StandardOutput = stdout,
            StandardError = stderr
        };
        await File.WriteAllTextAsync(transcriptPath, JsonSerializer.Serialize(transcript, _json), CancellationToken.None);
        await using var transcriptStream = File.OpenRead(transcriptPath);
        var transcriptHash = Convert.ToHexString(
            await SHA256.HashDataAsync(transcriptStream, CancellationToken.None));

        return new RestrictedPowerShellOutput(
            request.Command,
            exitCode,
            timedOut,
            stdout,
            stderr,
            transcriptPath,
            transcriptHash);
    }

    public async Task<PowerShellScriptOutput> ExecuteScriptAsync(
        Guid taskId, string parametersJson, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("PowerShell script tasks require Windows.");
        if (!_config.AllowUnrestrictedPowerShell)
            throw new UnauthorizedAccessException("Unrestricted PowerShell is disabled by the local agent configuration.");

        using var parameters = JsonDocument.Parse(parametersJson);
        if (!PowerShellScriptParameters.TryParse(parameters.RootElement, out var script, out var error))
            throw new UnauthorizedAccessException(error);

        var scriptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)));
        var startInfo = new ProcessStartInfo
        {
            FileName = _config.PowerShellExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

        var startedUtc = DateTimeOffset.UtcNow;
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("PowerShell could not be started.");

        var stdoutTask = ReadLimitedAsync(process.StandardOutput, _config.MaxPowerShellOutputBytes);
        var stderrTask = ReadLimitedAsync(process.StandardError, _config.MaxPowerShellOutputBytes);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* Process already exited. */ }
            await process.WaitForExitAsync(CancellationToken.None);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var completedUtc = DateTimeOffset.UtcNow;
        var exitCode = timedOut ? -1 : process.ExitCode;
        var outputTruncated = stdout.Truncated || stderr.Truncated;

        Directory.CreateDirectory(_config.TranscriptDirectory);
        var transcriptPath = Path.Combine(
            _config.TranscriptDirectory, $"{startedUtc:yyyyMMdd-HHmmss}-{taskId:D}.json");
        var transcript = new
        {
            TaskId = taskId,
            Agent = Environment.MachineName,
            Script = script,
            ScriptSha256 = scriptHash,
            StartedUtc = startedUtc,
            CompletedUtc = completedUtc,
            DurationMilliseconds = (long)(completedUtc - startedUtc).TotalMilliseconds,
            TimedOut = timedOut,
            ExitCode = exitCode,
            StandardOutput = stdout.Text,
            StandardError = stderr.Text,
            OutputTruncated = outputTruncated
        };
        await File.WriteAllTextAsync(transcriptPath, JsonSerializer.Serialize(transcript, _json), CancellationToken.None);
        await using var transcriptStream = File.OpenRead(transcriptPath);
        var transcriptHash = Convert.ToHexString(await SHA256.HashDataAsync(transcriptStream, CancellationToken.None));

        return new PowerShellScriptOutput(
            exitCode, timedOut, stdout.Text, stderr.Text, outputTruncated,
            scriptHash, transcriptPath, transcriptHash);
    }

    private static async Task<(string Text, bool Truncated)> ReadLimitedAsync(StreamReader reader, int maximumBytes)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory());
            if (count == 0) break;
            var remaining = maximumBytes - result.Length;
            if (remaining > 0) result.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining) truncated = true;
        }
        var captured = result.ToString();
        return (LimitUtf8(captured, maximumBytes),
            truncated || Encoding.UTF8.GetByteCount(captured) > maximumBytes);
    }

    private PowerShellRequest ParseAndValidate(string parametersJson)
    {
        var request = JsonSerializer.Deserialize<PowerShellRequest>(parametersJson, _json)
            ?? throw new JsonException("PowerShell request is empty.");
        if (!PowerShellCommands.Allowed.Contains(request.Command) ||
            !_config.EnabledPowerShellCommands.Contains(request.Command, StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The PowerShell command is not enabled by this agent's compiled/configured allowlist.");
        }

        var allowedArguments = request.Command switch
        {
            PowerShellCommands.GetProcess => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name" },
            PowerShellCommands.GetService => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name" },
            PowerShellCommands.GetHotFix => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id" },
            PowerShellCommands.GetNetTcpConnection => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "state" },
            PowerShellCommands.TestConnection => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "target", "count" },
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };
        if (request.Arguments.Keys.Any(key => !allowedArguments.Contains(key)))
        {
            throw new UnauthorizedAccessException("The request contains an argument that is not allowed for this command.");
        }

        if (request.Command.Equals(PowerShellCommands.TestConnection, StringComparison.OrdinalIgnoreCase))
        {
            var target = GetRequiredString(request.Arguments, "target");
            if (!_scope.IsTargetAllowed(target))
            {
                throw new UnauthorizedAccessException("The Test-Connection target is outside this agent's configured scope.");
            }

            if (request.Arguments.TryGetValue("count", out var count) &&
                (!count.TryGetInt32(out var countValue) || countValue is < 1 or > 4))
            {
                throw new UnauthorizedAccessException("Test-Connection count must be from 1 to 4.");
            }
        }

        foreach (var argument in request.Arguments.Where(item => !item.Key.Equals("count", StringComparison.OrdinalIgnoreCase)))
        {
            if (argument.Value.ValueKind != JsonValueKind.String || !IsSafeString(argument.Value.GetString()))
            {
                throw new UnauthorizedAccessException("PowerShell string arguments must be non-empty, at most 255 characters, and contain no control characters.");
            }
        }

        return request;
    }

    private static string BuildStaticScript(string requestBase64) => $$"""
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        try {
            $requestJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{requestBase64}}'))
            $request = $requestJson | ConvertFrom-Json
            switch ([string]$request.command) {
                'Get-ComputerInfo' {
                    $result = Get-ComputerInfo | Select-Object WindowsProductName, WindowsVersion, OsBuildNumber, CsName, CsDomain, TimeZone
                }
                'Get-Process' {
                    if ($null -ne $request.arguments.name) { $result = Get-Process -Name ([string]$request.arguments.name) }
                    else { $result = Get-Process }
                    $result = $result | Select-Object Id, ProcessName, CPU, WorkingSet, StartTime
                }
                'Get-Service' {
                    if ($null -ne $request.arguments.name) { $result = Get-Service -Name ([string]$request.arguments.name) }
                    else { $result = Get-Service }
                    $result = $result | Select-Object Name, DisplayName, Status, StartType
                }
                'Get-HotFix' {
                    if ($null -ne $request.arguments.id) { $result = Get-HotFix -Id ([string]$request.arguments.id) }
                    else { $result = Get-HotFix }
                    $result = $result | Select-Object HotFixID, Description, InstalledBy, InstalledOn
                }
                'Get-NetTCPConnection' {
                    if ($null -ne $request.arguments.state) { $result = Get-NetTCPConnection -State ([string]$request.arguments.state) }
                    else { $result = Get-NetTCPConnection }
                    $result = $result | Select-Object LocalAddress, LocalPort, RemoteAddress, RemotePort, State, OwningProcess
                }
                'Test-Connection' {
                    $count = if ($null -ne $request.arguments.count) { [int]$request.arguments.count } else { 1 }
                    $result = Test-Connection -ComputerName ([string]$request.arguments.target) -Count $count |
                        Select-Object Address, IPV4Address, ResponseTime, StatusCode
                }
                default { throw 'Command is not part of the compiled allowlist.' }
            }
            $result | ConvertTo-Json -Depth 5 -Compress
        }
        catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 1
        }
        """;

    private static string GetRequiredString(IReadOnlyDictionary<string, JsonElement> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String || !IsSafeString(value.GetString()))
        {
            throw new UnauthorizedAccessException($"PowerShell command requires a valid '{name}' argument.");
        }
        return value.GetString()!;
    }

    private static bool IsSafeString(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 255 && !value.Any(char.IsControl);

    private static string LimitUtf8(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return bytes.Length <= maxBytes ? value : Encoding.UTF8.GetString(bytes.AsSpan(0, maxBytes));
    }

    private sealed class PowerShellRequest
    {
        public string Command { get; set; } = string.Empty;
        public Dictionary<string, JsonElement> Arguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
