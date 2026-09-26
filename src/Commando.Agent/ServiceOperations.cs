using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Commando.Shared;

namespace Commando.Agent;

public sealed class ServiceOperations(AgentConfig config)
{
    private sealed record RecoveryJournal(string ServiceId, DateTimeOffset CreatedUtc);
    private sealed record CommandResult(int ExitCode, string Output, string Error);

    public async Task<object> GetStatusAsync(string parametersJson, CancellationToken cancellationToken)
    {
        var service = GetApprovedService(parametersJson, TaskKinds.ServiceStatus);
        return await GetStatusForServiceAsync(service, cancellationToken);
    }

    public async Task<object> PauseAsync(string parametersJson, CancellationToken cancellationToken)
    {
        var request = ParseRequest(parametersJson, TaskKinds.ServicePause);
        var service = GetApprovedService(request.ServiceId);
        if (!service.AllowPause)
            throw new UnauthorizedAccessException("Service pause is disabled by the local agent configuration.");
        if (request.DurationSeconds > config.TaskTimeoutSeconds - 20)
            throw new InvalidOperationException("Pause duration must leave at least 20 seconds for restoration.");
        if (File.Exists(config.RecoveryPath))
            throw new InvalidOperationException("A prior service recovery is pending. Restore it before another pause.");

        var initiallyActive = await IsActiveAsync(service, cancellationToken);
        if (!initiallyActive)
            throw new InvalidOperationException("The service was not active; no pause was performed.");
        if (service.TcpPort != 0 && !await IsTcpReachableAsync(service.TcpPort, cancellationToken))
            throw new InvalidOperationException("The configured local TCP check was already failing; no pause was performed.");

        var directory = Path.GetDirectoryName(config.RecoveryPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var journal = new RecoveryJournal(service.Id, DateTimeOffset.UtcNow);
        var pendingPath = config.RecoveryPath + ".tmp";
        await File.WriteAllTextAsync(pendingPath, JsonSerializer.Serialize(journal), cancellationToken);
        File.Move(pendingPath, config.RecoveryPath, overwrite: false);

        Exception? actionError = null;
        var stoppedUtc = DateTimeOffset.UtcNow;
        try
        {
            await ChangeStateAsync(service, active: false, cancellationToken);
            stoppedUtc = DateTimeOffset.UtcNow;
            await Task.Delay(TimeSpan.FromSeconds(request.DurationSeconds), cancellationToken);
        }
        catch (Exception exception)
        {
            actionError = exception;
        }

        using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await ChangeStateAsync(service, active: true, recoveryTimeout.Token);
            await WaitForTcpRecoveryAsync(service, recoveryTimeout.Token);
            File.Delete(config.RecoveryPath);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Service restoration failed for '{service.Id}'. Recovery journal remains at '{config.RecoveryPath}'.",
                exception);
        }

        if (actionError is not null)
            throw new InvalidOperationException("Service pause was interrupted; the service was restored.", actionError);

        return new
        {
            service.Id,
            service.ServiceName,
            StoppedUtc = stoppedUtc,
            RestoredUtc = DateTimeOffset.UtcNow,
            DurationSeconds = request.DurationSeconds,
            Restored = true
        };
    }

    public async Task RecoverPendingAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(config.RecoveryPath)) return;
        var journal = JsonSerializer.Deserialize<RecoveryJournal>(await File.ReadAllTextAsync(config.RecoveryPath, cancellationToken))
            ?? throw new InvalidOperationException("Service recovery journal is invalid.");
        var service = GetApprovedService(journal.ServiceId);
        await ChangeStateAsync(service, active: true, cancellationToken);
        await WaitForTcpRecoveryAsync(service, cancellationToken);
        File.Delete(config.RecoveryPath);
        Console.WriteLine($"Recovered service '{service.Id}' from a prior interrupted pause.");
    }

    private LocalService GetApprovedService(string parametersJson, string kind)
    {
        using var document = JsonDocument.Parse(parametersJson);
        return GetApprovedService(ParseRequest(document.RootElement, kind).ServiceId);
    }

    private LocalService GetApprovedService(string id) =>
        config.Services.SingleOrDefault(service => service.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new UnauthorizedAccessException("Service is not in this agent's local allowlist.");

    private static ServiceTaskRequest ParseRequest(string parametersJson, string kind)
    {
        using var document = JsonDocument.Parse(parametersJson);
        return ParseRequest(document.RootElement, kind);
    }

    private static ServiceTaskRequest ParseRequest(JsonElement parameters, string kind) =>
        ServiceTaskParameters.TryParse(parameters, kind, out var request, out var error)
            ? request!
            : throw new UnauthorizedAccessException(error);

    private async Task<object> GetStatusForServiceAsync(LocalService service, CancellationToken cancellationToken)
    {
        var active = await IsActiveAsync(service, cancellationToken);
        bool? tcpReachable = service.TcpPort == 0 ? null : await IsTcpReachableAsync(service.TcpPort, cancellationToken);
        return new
        {
            service.Id,
            service.ServiceName,
            Active = active,
            TcpPort = service.TcpPort == 0 ? null : (int?)service.TcpPort,
            TcpReachable = tcpReachable,
            Healthy = active && (tcpReachable ?? true),
            CapturedUtc = DateTimeOffset.UtcNow
        };
    }

    private static async Task<bool> IsActiveAsync(LocalService service, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var result = await RunCommandAsync(ServiceExecutable(), ["query", service.ServiceName], cancellationToken);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Service query failed: {result.Error}");
            var match = Regex.Match(result.Output, @"STATE\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            if (!match.Success) throw new InvalidOperationException("Could not read Windows service state.");
            return match.Groups[1].Value == "4";
        }

        if (OperatingSystem.IsLinux())
        {
            var result = await RunCommandAsync(ServiceExecutable(), ["is-active", service.ServiceName], cancellationToken);
            if (result.ExitCode is not (0 or 3))
                throw new InvalidOperationException($"systemctl is-active failed: {result.Error}");
            return result.Output.Trim().Equals("active", StringComparison.OrdinalIgnoreCase);
        }

        throw new PlatformNotSupportedException("Service checks require Windows or Linux.");
    }

    private static async Task ChangeStateAsync(LocalService service, bool active, CancellationToken cancellationToken)
    {
        var executable = ServiceExecutable();
        var arguments = new[] { active ? "start" : "stop", service.ServiceName };
        var result = await RunCommandAsync(executable, arguments, cancellationToken);
        if (result.ExitCode != 0 && await IsActiveAsync(service, cancellationToken) != active)
            throw new InvalidOperationException($"Service state change failed: {result.Error}");

        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await IsActiveAsync(service, cancellationToken) == active) return;
            await Task.Delay(500, cancellationToken);
        }
        throw new TimeoutException($"Service '{service.Id}' did not reach the expected state.");
    }

    private static async Task<bool> IsTcpReachableAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task WaitForTcpRecoveryAsync(LocalService service, CancellationToken cancellationToken)
    {
        if (service.TcpPort == 0) return;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await IsTcpReachableAsync(service.TcpPort, cancellationToken)) return;
            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException($"Service '{service.Id}' restarted, but local TCP port {service.TcpPort} is not reachable.");
    }

    public static async Task<object> ListLinuxServicesAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) return Array.Empty<string>();
        var result = await RunCommandAsync(ServiceExecutable(), ["list-units", "--type=service", "--all", "--no-legend", "--plain"], cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Service inventory failed: {result.Error}");
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3000).ToArray();
    }

    private static async Task<CommandResult> RunCommandAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {executable}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string ServiceExecutable() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.SystemDirectory, "sc.exe")
        : OperatingSystem.IsLinux()
            ? "/usr/bin/systemctl"
            : throw new PlatformNotSupportedException("Service operations require Windows or Linux.");
}
