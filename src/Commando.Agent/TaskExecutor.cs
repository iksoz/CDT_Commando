using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Commando.Shared;

namespace Commando.Agent;

public sealed record ExecutionResult(bool Success, object? Output, string? Error, bool StopRequested = false);

public sealed class TaskExecutor
{
    private readonly AgentConfig _config;
    private readonly RestrictedPowerShellExecutor? _powerShell;
    private readonly ServiceOperations _services;

    public TaskExecutor(AgentConfig config)
    {
        _config = config;
        _powerShell = OperatingSystem.IsWindows() ? new RestrictedPowerShellExecutor(config) : null;
        _services = new ServiceOperations(config);
    }

    public Task RecoverPendingAsync(CancellationToken cancellationToken) => _services.RecoverPendingAsync(cancellationToken);

    public async Task<ExecutionResult> ExecuteAsync(TaskEnvelope task, CancellationToken cancellationToken)
    {
        if (!TaskKinds.Allowed.Contains(task.Kind))
        {
            return new ExecutionResult(false, null, "The requested task kind is not allowlisted.");
        }

        try
        {
            return task.Kind.ToLowerInvariant() switch
            {
                TaskKinds.SystemInfo => new ExecutionResult(true, GetSystemInfo(), null),
                TaskKinds.ListProcesses => new ExecutionResult(true, GetProcesses(), null),
                TaskKinds.ListServices => new ExecutionResult(true, await GetServicesAsync(cancellationToken), null),
                TaskKinds.NetworkConnections => new ExecutionResult(true, GetNetworkConnections(), null),
                TaskKinds.HashFile => new ExecutionResult(true, await HashFileAsync(task.ParametersJson, cancellationToken), null),
                TaskKinds.RestrictedPowerShell => await ExecutePowerShellAsync(task, cancellationToken),
                TaskKinds.PowerShellScript => await ExecutePowerShellScriptAsync(task, cancellationToken),
                TaskKinds.ServiceStatus => new ExecutionResult(true, await _services.GetStatusAsync(task.ParametersJson, cancellationToken), null),
                TaskKinds.ServicePause => new ExecutionResult(true, await _services.PauseAsync(task.ParametersJson, cancellationToken), null),
                TaskKinds.StopAgent => new ExecutionResult(true, new { message = "Agent stop acknowledged." }, null, true),
                _ => new ExecutionResult(false, null, "Unsupported task kind.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ExecutionResult(false, null, "Task timed out or was cancelled.");
        }
        catch (Exception exception)
        {
            return new ExecutionResult(false, null, exception.Message);
        }
    }

    private async Task<ExecutionResult> ExecutePowerShellAsync(TaskEnvelope task, CancellationToken cancellationToken)
    {
        if (_powerShell is null)
            return new ExecutionResult(false, null, "Restricted PowerShell is available only on Windows.");
        var output = await _powerShell.ExecuteAsync(task.Id, task.ParametersJson, cancellationToken);
        var success = !output.TimedOut && output.ExitCode == 0;
        var error = success ? null : output.TimedOut ? "PowerShell task timed out." : output.StandardError;
        return new ExecutionResult(success, output, error);
    }

    private async Task<ExecutionResult> ExecutePowerShellScriptAsync(TaskEnvelope task, CancellationToken cancellationToken)
    {
        if (_powerShell is null)
            return new ExecutionResult(false, null, "PowerShell script tasks require Windows.");
        var output = await _powerShell.ExecuteScriptAsync(task.Id, task.ParametersJson, cancellationToken);
        var success = !output.TimedOut && output.ExitCode == 0;
        var error = success ? null : output.TimedOut ? "PowerShell script timed out or was cancelled." : output.StandardError;
        return new ExecutionResult(success, output, error);
    }

    private static object GetSystemInfo()
    {
        var drives = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => new
            {
                drive.Name,
                drive.DriveType,
                drive.DriveFormat,
                TotalSizeBytes = drive.TotalSize,
                FreeSpaceBytes = drive.AvailableFreeSpace
            })
            .ToArray();

        return new
        {
            Hostname = Environment.MachineName,
            User = $"{Environment.UserDomainName}\\{Environment.UserName}",
            Os = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Dotnet = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            WorkingSetBytes = Environment.WorkingSet,
            Is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
            IsElevated = OperatingSystem.IsWindows() ? IsElevated() : (bool?)null,
            Drives = drives,
            CapturedUtc = DateTimeOffset.UtcNow
        };
    }

    private static object[] GetProcesses()
    {
        return Process.GetProcesses()
            .OrderBy(process => process.Id)
            .Take(1000)
            .Select(process =>
            {
                try
                {
                    return (object)new
                    {
                        process.Id,
                        process.ProcessName,
                        SessionId = OperatingSystem.IsWindows() ? process.SessionId : -1,
                        WorkingSetBytes = process.WorkingSet64,
                        StartTimeUtc = process.StartTime.ToUniversalTime()
                    };
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    return new { process.Id, process.ProcessName };
                }
                finally
                {
                    process.Dispose();
                }
            })
            .ToArray();
    }

    private static async Task<object> GetServicesAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return await ServiceOperations.ListLinuxServicesAsync(cancellationToken);
        }

        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null)
        {
            return Array.Empty<object>();
        }

        var results = new List<object>();
        foreach (var name in services.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase).Take(3000))
        {
            using var service = services.OpenSubKey(name);
            if (service is null)
            {
                continue;
            }

            var type = service.GetValue("Type");
            if (type is not int typeValue || (typeValue & 0x30) == 0)
            {
                continue;
            }

            results.Add(new
            {
                Name = name,
                DisplayName = service.GetValue("DisplayName")?.ToString(),
                ImagePath = service.GetValue("ImagePath")?.ToString(),
                Start = service.GetValue("Start"),
                Type = typeValue,
                ObjectName = service.GetValue("ObjectName")?.ToString()
            });
        }

        return results.ToArray();
    }

    private static object GetNetworkConnections()
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        var tcp = properties.GetActiveTcpConnections().Take(3000).Select(connection => new
        {
            Local = connection.LocalEndPoint.ToString(),
            Remote = connection.RemoteEndPoint.ToString(),
            State = connection.State.ToString()
        });
        var listeners = properties.GetActiveTcpListeners().Take(3000).Select(endpoint => endpoint.ToString());
        var udp = properties.GetActiveUdpListeners().Take(3000).Select(endpoint => endpoint.ToString());
        return new { TcpConnections = tcp, TcpListeners = listeners, UdpListeners = udp };
    }

    private async Task<object> HashFileAsync(string parametersJson, CancellationToken cancellationToken)
    {
        using var parameters = JsonDocument.Parse(parametersJson);
        if (!parameters.RootElement.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("The hash_file task requires a path string.");
        }

        var requestedPath = Path.GetFullPath(pathElement.GetString()!);
        if (!IsUnderAllowedRoot(requestedPath))
        {
            throw new UnauthorizedAccessException("The requested file is outside the configured hash roots.");
        }

        var info = new FileInfo(requestedPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The requested file does not exist.", requestedPath);
        }

        RejectReparsePoints(requestedPath);

        await using var stream = new FileStream(requestedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return new
        {
            Path = requestedPath,
            Sha256 = Convert.ToHexString(hash),
            SizeBytes = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc
        };
    }

    private bool IsUnderAllowedRoot(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var root in _config.AllowedHashRoots)
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (path.Equals(normalizedRoot, comparison) ||
                path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison))
            {
                return true;
            }
        }

        return false;
    }

    private static void RejectReparsePoints(string path)
    {
        var root = Path.GetPathRoot(path) ?? throw new UnauthorizedAccessException("File path has no root.");
        var current = root;
        foreach (var component in path[root.Length..].Split(
                     Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Hashing through symbolic links or reparse points is disabled.");
        }
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
