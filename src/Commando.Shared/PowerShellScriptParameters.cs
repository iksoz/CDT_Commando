using System.Text.Json;

namespace Commando.Shared;

public static class PowerShellScriptParameters
{
    public const int MaximumScriptCharacters = 10_000;

    public static bool TryParse(JsonElement parameters, out string script, out string error)
    {
        script = string.Empty;
        error = string.Empty;
        if (parameters.ValueKind != JsonValueKind.Object ||
            parameters.EnumerateObject().Count() != 1 ||
            !parameters.TryGetProperty("script", out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            error = "powershell_script requires only a 'script' string.";
            return false;
        }

        script = value.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(script) || script.Length > MaximumScriptCharacters)
        {
            error = $"PowerShell script must be non-empty and at most {MaximumScriptCharacters} characters.";
            return false;
        }

        return true;
    }
}
