using System.Text.Json;
using Commando.Agent;
using Commando.Shared;

var configPath = GetOption(args, "--config") ?? "commando-agent.json";
var runOnce = args.Contains("--once", StringComparer.OrdinalIgnoreCase);

try
{
    var config = AgentConfig.Load(configPath);
    var executor = new TaskExecutor(config);
    await executor.RecoverPendingAsync(CancellationToken.None);
    using var client = new AgentClient(config);
    await client.EnrollAsync(CancellationToken.None);
    Console.WriteLine($"Commando agent {client.State.AgentId:D} enrolled. Competition expires {client.State.CompetitionExpiresUtc:O}.");

    if (client.State.CompetitionExpiresUtc <= DateTimeOffset.UtcNow)
    {
        Console.Error.WriteLine("Competition window has expired; exiting.");
        return 2;
    }

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    do
    {
        var task = await client.GetNextTaskAsync(shutdown.Token);
        if (task is not null)
        {
            if (task.AgentId != client.State.AgentId || task.ExpiresUtc <= DateTimeOffset.UtcNow ||
                !TaskSigning.Verify(task, client.State.AgentToken))
            {
                Console.Error.WriteLine($"Rejected invalid or expired task {task.Id:D}.");
            }
            else
            {
                Console.WriteLine($"Executing approved task {task.Id:D}: {task.Kind}");
                var started = DateTimeOffset.UtcNow;
                using var taskTimeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                taskTimeout.CancelAfter(TimeSpan.FromSeconds(config.TaskTimeoutSeconds));
                ExecutionResult execution;
                if (task.Kind != TaskKinds.StopAgent &&
                    (await client.GetControlAsync(taskTimeout.Token)).State != "live")
                {
                    execution = new ExecutionResult(false, null, "Event is not live; task was not executed.");
                }
                else
                {
                    using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                    var monitor = task.Kind == TaskKinds.StopAgent
                        ? Task.CompletedTask
                        : MonitorControlAsync(client, taskTimeout, monitorStop.Token);
                    try
                    {
                        execution = await executor.ExecuteAsync(task, taskTimeout.Token);
                    }
                    finally
                    {
                        monitorStop.Cancel();
                        await monitor;
                    }
                }
                var completed = DateTimeOffset.UtcNow;
                var outputJson = JsonSerializer.Serialize(
                    execution.Output ?? new { },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                await client.SubmitResultAsync(task.Id, new TaskResultRequest(
                    execution.Success,
                    outputJson,
                    execution.Error,
                    started,
                    completed), shutdown.Token);

                if (execution.StopRequested)
                {
                    Console.WriteLine("Server requested a clean agent stop.");
                    break;
                }
            }
        }

        if (!runOnce)
        {
            await Task.Delay(TimeSpan.FromSeconds(config.PollSeconds), shutdown.Token);
        }
    } while (!runOnce && !shutdown.IsCancellationRequested);

    return 0;
}
catch (CompetitionExpiredException)
{
    Console.Error.WriteLine("Competition window has expired; exiting.");
    return 2;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Agent stopped.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Commando agent failed: {exception.Message}");
    return 1;
}

static string? GetOption(string[] arguments, string name)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }

    return null;
}

static async Task MonitorControlAsync(
    AgentClient client, CancellationTokenSource taskTimeout, CancellationToken cancellationToken)
{
    try
    {
        while (!cancellationToken.IsCancellationRequested && !taskTimeout.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var control = await client.GetControlAsync(cancellationToken);
            if (control.State != "live")
            {
                taskTimeout.Cancel();
                return;
            }
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || taskTimeout.IsCancellationRequested)
    {
        // The task completed or was cancelled.
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Control check failed; cancelling task: {exception.Message}");
        taskTimeout.Cancel();
    }
}
