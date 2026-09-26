using System.Text.Json;
using System.Text.RegularExpressions;

namespace Commando.Shared;

public sealed record ServiceTaskRequest(string ServiceId, int DurationSeconds);

public static partial class ServiceTaskParameters
{
    public static bool TryParse(JsonElement parameters, string kind, out ServiceTaskRequest? request, out string error)
    {
        request = null;
        error = string.Empty;
        if (kind is not (TaskKinds.ServiceStatus or TaskKinds.ServicePause) ||
            parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("serviceId", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String ||
            !ServiceIdPattern().IsMatch(idElement.GetString() ?? string.Empty))
        {
            error = "Service task requires a valid serviceId.";
            return false;
        }

        var duration = 0;
        foreach (var property in parameters.EnumerateObject())
        {
            if (property.Name is not ("serviceId" or "durationSeconds"))
            {
                error = "Service task contains an unexpected property.";
                return false;
            }
        }

        if (kind == TaskKinds.ServicePause)
        {
            if (!parameters.TryGetProperty("durationSeconds", out var durationElement) ||
                durationElement.ValueKind != JsonValueKind.Number ||
                !durationElement.TryGetInt32(out duration) || duration is < 5 or > 60)
            {
                error = "service_pause requires durationSeconds from 5 to 60.";
                return false;
            }
        }
        else if (parameters.TryGetProperty("durationSeconds", out _))
        {
            error = "service_status does not accept durationSeconds.";
            return false;
        }

        request = new ServiceTaskRequest(idElement.GetString()!, duration);
        return true;
    }

    public static bool IsAllowed(JsonElement parameters, IEnumerable<string> allowedServiceIds, string kind) =>
        TryParse(parameters, kind, out var request, out _) &&
        allowedServiceIds.Contains(request!.ServiceId, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceIdPattern();
}
