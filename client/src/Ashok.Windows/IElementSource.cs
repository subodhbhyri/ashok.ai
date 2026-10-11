using Ashok.Core.Capture;

namespace Ashok.Windows;

/// <summary>Reads raw elements from a window. The only place that touches UI Automation.</summary>
public interface IElementSource
{
    IReadOnlyList<RawElement> Read(IntPtr windowHandle);
}
