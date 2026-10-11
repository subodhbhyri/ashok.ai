namespace Ashok.Core.Capture;

/// <summary>
/// What the platform walker read for one element. There is deliberately no value field:
/// edit-field contents never enter the pipeline, so they cannot leak.
/// </summary>
public sealed record RawElement(
    string Role,
    string Name,
    string? AutomationId,
    int X,
    int Y,
    int Width,
    int Height,
    bool Enabled,
    string? Toggle,
    bool IsPassword,
    bool IsOffscreen);
