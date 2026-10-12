using System.Runtime.InteropServices;
using Ashok.Windows;

namespace Ashok.Windows.Tests;

public class CaptureRepairGuardTests
{
    private const int EventSubscribersFailed = unchecked((int)0x80040201);
    private static readonly TimeSpan NoDelay = TimeSpan.Zero;

    private static readonly IReadOnlyList<int> Elements = [1, 2, 3];

    /// <summary>A reader that throws for the first <paramref name="failures"/> attempts, then returns elements.</summary>
    private sealed class Flaky(int failures)
    {
        public int Attempts;

        public IReadOnlyList<int> Read()
        {
            if (++Attempts <= failures) throw new COMException("An event was unable to invoke any of the subscribers", EventSubscribersFailed);
            return Elements;
        }
    }

    [Fact]
    public void Three_bad_reads_reset_once_and_the_next_read_returns_elements()
    {
        var resets = new List<int>();
        var guard = new CaptureRepairGuard(resets.Add, NoDelay);
        var reader = new Flaky(failures: 6); // three reads, each failing its attempt and its one retry

        for (var read = 1; read <= 3; read++)
            Assert.Throws<COMException>(() => guard.Run(1, reader.Read, l => l.Count == 0));

        Assert.Equal([EventSubscribersFailed], resets);
        Assert.Equal(1, guard.Resets);
        Assert.Equal(Elements, guard.Run(1, reader.Read, l => l.Count == 0));
        Assert.Equal([EventSubscribersFailed], resets);
    }

    [Fact]
    public void A_bad_read_is_retried_once_and_a_rescue_does_not_count()
    {
        var resets = 0;
        var guard = new CaptureRepairGuard(_ => resets++, NoDelay);
        var reader = new Flaky(failures: 1);

        Assert.Equal(Elements, guard.Run(1, reader.Read, l => l.Count == 0));

        Assert.Equal(2, reader.Attempts);
        Assert.Equal(0, resets);
    }

    [Fact]
    public void A_good_read_in_between_restarts_the_count()
    {
        var resets = 0;
        var guard = new CaptureRepairGuard(_ => resets++, NoDelay);
        Func<IReadOnlyList<int>> bad = () => throw new TimeoutException("UIA Timeout");

        for (var round = 0; round < 3; round++)
        {
            Assert.Throws<TimeoutException>(() => guard.Run(1, bad));
            Assert.Throws<TimeoutException>(() => guard.Run(1, bad));
            guard.Run(1, () => Elements);
        }

        Assert.Equal(0, resets);
    }

    [Fact]
    public void Counts_are_per_window()
    {
        var resets = 0;
        var guard = new CaptureRepairGuard(_ => resets++, NoDelay);
        Func<IReadOnlyList<int>> bad = () => throw new TimeoutException();

        for (var i = 0; i < 2; i++)
        {
            Assert.Throws<TimeoutException>(() => guard.Run(1, bad));
            Assert.Throws<TimeoutException>(() => guard.Run(2, bad));
        }

        Assert.Equal(0, resets);
    }

    [Fact]
    public void Empty_is_bad_only_for_a_window_that_returned_elements_before()
    {
        var resets = 0;
        var guard = new CaptureRepairGuard(_ => resets++, NoDelay);
        IReadOnlyList<int> empty = [];

        for (var i = 0; i < 5; i++) Assert.Empty(guard.Run(1, () => empty, l => l.Count == 0)); // never had content: fine
        Assert.Equal(0, resets);

        guard.Run(1, () => Elements, l => l.Count == 0);
        for (var i = 0; i < 3; i++) Assert.Empty(guard.Run(1, () => empty, l => l.Count == 0)); // now it is a bad read

        Assert.Equal(1, resets);
    }

    [Fact]
    public void A_swallowing_caller_gets_the_fallback_and_still_resets()
    {
        var resets = 0;
        var guard = new CaptureRepairGuard(_ => resets++, NoDelay);
        Func<string?> bad = () => throw new COMException("x", EventSubscribersFailed);

        for (var i = 0; i < 3; i++)
            Assert.Null(guard.Run(0, bad, swallow: true, whenSwallowed: null));

        Assert.Equal(1, resets);
    }

    [Fact]
    public void Other_exceptions_are_not_repaired()
    {
        var guard = new CaptureRepairGuard(_ => Assert.Fail("reset"), NoDelay);
        var attempts = 0;

        Assert.Throws<InvalidOperationException>(() => guard.Run<int>(1, () =>
        {
            attempts++;
            throw new InvalidOperationException();
        }));

        Assert.Equal(1, attempts);
    }
}
