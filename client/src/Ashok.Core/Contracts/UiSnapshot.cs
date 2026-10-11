using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ashok.Core.Contracts;

/// <summary>Mirrors docs/contracts/ui-snapshot.schema.json.</summary>
public sealed record UiSnapshot(
    OsInfo Os,
    ForegroundInfo Foreground,
    IReadOnlyList<UiElement> Elements,
    int Redacted,
    bool Truncated);

public sealed record OsInfo(string Edition, string Build);

public sealed record ForegroundInfo(string Process, string Title);

/// <param name="Ref">Only valid for the snapshot it appears in.</param>
/// <param name="Bounds">[x, y, width, height] in physical screen pixels.</param>
/// <param name="Toggle">"on", "off", "indeterminate" or null when the element is not a toggle.</param>
public sealed record UiElement(
    string Ref,
    string Role,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AutomationId,
    int[] Bounds,
    bool Enabled,
    string? Toggle);

public static class SnapshotJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
