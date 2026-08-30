using Iciclecreek.Terminal;

namespace Iciclecreek.Avalonia.Terminal.Tests;

/// <summary>
/// The render throttle's frame rate is global mutable state reachable by any host that can reference the
/// assembly, which is what makes the guard rail worth asserting: a rejected value has to leave the previous
/// rate standing, because the alternative is every terminal in the app repainting at the wrong cadence — or
/// not at all — with nothing on screen to say why.
///
/// <para>No Avalonia here. The property is arithmetic and validation; the coordinated frame it feeds needs a
/// UI thread that these assertions do not.</para>
/// </summary>
[TestClass]
public class RenderFrameRateTests
{
    private int _original;

    // Captured and put back rather than left wherever a test happened to leave it. A stray rate would not
    // fail anything downstream, it would quietly change the cadence the rest of the suite renders at, which
    // is the worse outcome of the two.
    [TestInitialize]
    public void CaptureOriginal() => _original = TerminalRenderThrottle.TargetFrameRate;

    [TestCleanup]
    public void RestoreOriginal() => TerminalRenderThrottle.TargetFrameRate = _original;

    [TestMethod]
    public void Defaults_To_Thirty_Frames_Per_Second()
    {
        _original.Should().Be(30);
    }

    [TestMethod]

    [DataRow(1)]
    [DataRow(30)]
    [DataRow(60)]
    [DataRow(1000)]
    public void Accepts_A_Rate_Inside_The_Range(int framesPerSecond)
    {
        TerminalRenderThrottle.TargetFrameRate = framesPerSecond;

        TerminalRenderThrottle.TargetFrameRate.Should().Be(framesPerSecond);
    }

    // Zero is the case the range exists for. It divides into an infinite interval — not a duration any frame
    // can be scheduled from — and it would fail deep inside ScheduleFrame on the PTY read thread rather than
    // at the assignment the host actually got wrong. A negative rate is quieter and no better: it yields a
    // negative interval, which compares as already elapsed and defeats the throttle completely.
    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(1001)]
    public void Rejects_A_Rate_Outside_The_Range(int framesPerSecond)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => TerminalRenderThrottle.TargetFrameRate = framesPerSecond);
    }

    [TestMethod]
    public void A_Rejected_Rate_Leaves_The_Previous_One_In_Place()
    {
        TerminalRenderThrottle.TargetFrameRate = 45;

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => TerminalRenderThrottle.TargetFrameRate = 0);

        TerminalRenderThrottle.TargetFrameRate.Should().Be(45);
    }
}
