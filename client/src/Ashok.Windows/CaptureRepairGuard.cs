using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using FlaUI.Core.Exceptions;

namespace Ashok.Windows;

/// <summary>
/// Makes UI Automation reads repair themselves. A bad read is a COM, element-not-available or timeout error, or
/// an empty result from a window that returned elements before. A bad read is retried once after
/// <see cref="DefaultRetryDelay"/>; <see cref="ResetAfter"/> consecutive bad reads of the same window call
/// <c>reset</c> (the owner disposes and recreates its automation object) and start the count again.
/// A good read clears the count for that window.
///
/// Not thread-safe by design: callers are on the capture thread, which the runner already serializes.
/// </summary>
public sealed class CaptureRepairGuard(Action<int> reset, TimeSpan? retryDelay = null)
{
    public const int ResetAfter = 3;
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(150);

    private readonly Dictionary<long, int> _bad = [];
    private readonly HashSet<long> _hadContent = [];
    private readonly TimeSpan _delay = retryDelay ?? DefaultRetryDelay;

    public int Resets { get; private set; }

    /// <param name="key">Which window this read is for (any stable id; 0 when there is only one).</param>
    /// <param name="once">One attempt at the read.</param>
    /// <param name="isEmpty">When set, an empty result counts as bad once this key has returned content.</param>
    /// <param name="swallow">After the retry also fails: return <paramref name="whenSwallowed"/> instead of rethrowing.</param>
    public T Run<T>(long key, Func<T> once, Func<T, bool>? isEmpty = null, bool swallow = false, T? whenSwallowed = default)
    {
        var first = Attempt(key, once, isEmpty);
        if (!first.Bad) return Good(key, first.Value!, isEmpty);

        Thread.Sleep(_delay);
        var second = Attempt(key, once, isEmpty);
        if (!second.Bad) return Good(key, second.Value!, isEmpty);

        var count = _bad[key] = _bad.GetValueOrDefault(key) + 1;
        if (count >= ResetAfter)
        {
            _bad.Clear();
            Resets++;
            reset(second.Error?.HResult ?? first.Error?.HResult ?? 0);
        }

        if (second.Error is null) return second.Value!;
        if (swallow) return whenSwallowed!;
        ExceptionDispatchInfo.Capture(second.Error).Throw();
        return default!;
    }

    private (bool Bad, T? Value, Exception? Error) Attempt<T>(long key, Func<T> once, Func<T, bool>? isEmpty)
    {
        try
        {
            var value = once();
            return (isEmpty is not null && isEmpty(value) && _hadContent.Contains(key), value, null);
        }
        catch (Exception e) when (e is COMException or ElementNotAvailableException or TimeoutException)
        {
            return (true, default, e);
        }
    }

    private T Good<T>(long key, T value, Func<T, bool>? isEmpty)
    {
        _bad.Remove(key);
        if (isEmpty is not null && !isEmpty(value)) _hadContent.Add(key);
        return value;
    }
}
