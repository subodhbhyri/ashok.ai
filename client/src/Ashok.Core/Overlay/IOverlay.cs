namespace Ashok.Core.Overlay;

/// <summary>
/// Draws a ring around a region of the screen. The overlay never takes focus and never intercepts
/// mouse input: clicks go straight through to whatever is underneath.
/// </summary>
public interface IOverlay
{
    /// <summary>
    /// Ring geometry, in physical pixels at every DPI. The ring's outer edge is exactly the target
    /// inflated by <see cref="OverlayGeometry.OuterInflation"/>; implementations must honour this so
    /// alignment can be measured from a screen capture.
    /// </summary>
    void Show(ScreenRect target);

    void Hide();

    /// <summary>
    /// When true the ring pulses. When false it is drawn at full opacity in
    /// <see cref="OverlayGeometry.RingColorRgb"/>, which is what alignment checks measure.
    /// </summary>
    bool Pulse { get; set; }
}

public static class OverlayGeometry
{
    /// <summary>Gap between the target's edge and the ring's inner edge.</summary>
    public const int RingPaddingPx = 6;

    public const int RingThicknessPx = 6;

    /// <summary>Saturated magenta, unlikely to appear in ordinary UI. 0xRRGGBB.</summary>
    public const int RingColorRgb = 0xFF00AA;

    /// <summary>How far the ring's outer edge sits outside the target on every side.</summary>
    public const int OuterInflation = RingPaddingPx + RingThicknessPx;

    public static ScreenRect OuterBox(ScreenRect target) => target.Inflate(OuterInflation);
}
