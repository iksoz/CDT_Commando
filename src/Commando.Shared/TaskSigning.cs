using System.Security.Cryptography;
using System.Text;

namespace Commando.Shared;

public static class TaskSigning
{
    public static string Sign(TaskEnvelope task, string agentToken)
    {
        var bytes = Encoding.UTF8.GetBytes(Canonicalize(task));
        var key = Convert.FromBase64String(agentToken);
        return Convert.ToBase64String(HMACSHA256.HashData(key, bytes));
    }

    public static bool Verify(TaskEnvelope task, string agentToken)
    {
        try
        {
            var expected = Convert.FromBase64String(Sign(task with { Signature = string.Empty }, agentToken));
            var supplied = Convert.FromBase64String(task.Signature);
            return CryptographicOperations.FixedTimeEquals(expected, supplied);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Canonicalize(TaskEnvelope task) => string.Join('|',
        task.Id.ToString("D"),
        task.AgentId.ToString("D"),
        task.Kind,
        task.ParametersJson,
        task.CreatedUtc.ToUniversalTime().ToString("O"),
        task.ExpiresUtc.ToUniversalTime().ToString("O"));
}
