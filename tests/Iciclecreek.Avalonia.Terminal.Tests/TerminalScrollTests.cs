using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Wheel scrolling over a terminal. The reported symptom was a terminal that "struggles to be allowed to
/// scroll" with a touchpad, and there were two mechanisms behind it:
///
///  1. The view was hit-testable only over the pixels it had DRAWN — Avalonia's rule, the same one that
///     makes a Grid with no Background invisible to the pointer — so wheel events over blank space never
///     reached it.
///  2. Fractional trackpad deltas were truncated to an int per event, and each ~0.05 event rounded to zero.
///
/// These drive the REAL input path (headless window → hit test → routed event) rather than calling the
/// handler, so a pass means the wheel actually reaches the control and the buffer actually moves.
/// </summary>
[TestClass]
public class TerminalScrollTests
{
    /// <summary>Fill the view's buffer with scrollback, without spawning a shell.</summary>
    private static void WriteLines(TerminalView view, int count)
    {
        for (var i = 0; i < count; i++)
            view.Terminal.WriteLine($"line {i}");
        view.Terminal.Buffer.ScrollToBottom();
    }

    /// <summary>A shown, laid-out terminal parked at the tail of 300 lines of scrollback.</summary>
    private static (Window Window, TerminalView View) ShowTerminal()
    {
        // No shell. Process defaults to bash (cmd.exe on Windows) and OnLoaded spawns it, so a view that is
        // merely SHOWN gets a real PTY whose banner and prompt land in the buffer whenever the shell gets
        // round to writing them — on a thread these tests don't control. That moves the tail (MaxScrollback
        // is Buffer.Length - Rows) at an arbitrary point, including between the baseline below and a test's
        // assertion.
        var view = new TerminalView { Process = "" };
        var window = HeadlessUi.Show(view);
        WriteLines(view, 300);

        HeadlessUi.Pump(window);
        view.FollowTail();

        view.IsLive.Should().BeFalse("nothing but the test may write to the buffer — see Process above");
        view.MaxScrollback.Should().BeGreaterThan(10, "the buffer was filled with scrollback to scroll through");
        LinesAboveTail(view).Should().Be(0, "the view starts parked at the tail");
        return (window, view);
    }

    /// <summary>How far up the scrollback the view is sitting. Assertions are written against this rather
    /// than an absolute ViewportY: sizing the view re-grids the terminal to a new row count, which moves the
    /// tail, so an absolute baseline read before layout has settled measures a position that will move.</summary>
    private static int LinesAboveTail(TerminalView view) => view.MaxScrollback - view.ViewportY;

    // No dispatch here: every caller is an [AvaloniaTest], so the body is already on the UI thread.
    // This just owns the window's lifetime.
    private static void WithTerminal(Action<Window, TerminalView> body)
    {
        var (window, view) = ShowTerminal();
        try { body(window, view); }
        finally { window.Close(); }
    }

    private static void Wheel(Window window, double deltaY, int times = 1)
    {
        for (var i = 0; i < times; i++)
            window.MouseWheel(new Point(50, 50), new Vector(0, deltaY));
    }

    [AvaloniaTest]
    public void NotchedWheel_ScrollsThreeLines()
    {
        WithTerminal((window, view) =>
        {
            Wheel(window, 1);                       // one detent, wheel-up
            HeadlessUi.Pump(window);

            LinesAboveTail(view).Should().Be(3);
        });
    }

    [AvaloniaTest]
    public void TrackpadFractions_AccumulateInsteadOfRoundingToNothing()
    {
        WithTerminal((window, view) =>
        {
            // A slow two-finger drag: twenty ~0.05-line events. Truncating each one on its own — what the
            // control used to do — leaves the viewport exactly where it started.
            Wheel(window, 0.05, times: 20);
            HeadlessUi.Pump(window);

            LinesAboveTail(view).Should().Be(3, "20 × 0.05 × 3 lines = 3 whole lines of travel");
        });
    }

    [AvaloniaTest]
    public void WheelOverBlankSpace_StillReachesTheTerminal()
    {
        WithTerminal((window, view) =>
        {
            // Far below the last row and right of every line — nothing is drawn here, which is precisely
            // where the pointer used to fall straight through to whatever sat behind the view.
            window.MouseWheel(new Point(window.Width - 20, window.Height - 20), new Vector(0, 1));
            HeadlessUi.Pump(window);

            LinesAboveTail(view).Should().Be(3);
        });
    }

    [AvaloniaTest]
    public void ReversingDirection_AnswersOnTheFirstEvent()
    {
        WithTerminal((window, view) =>
        {
            Wheel(window, 1, times: 3);             // up 9 lines, well clear of the tail
            HeadlessUi.Pump(window);
            LinesAboveTail(view).Should().Be(9);

            // Leave a third of a line owed upward, then reverse: the stale remainder must not have to be
            // paid off before the downward travel starts registering.
            Wheel(window, 0.1);
            Wheel(window, -0.1, times: 10);
            HeadlessUi.Pump(window);

            LinesAboveTail(view).Should().Be(6, "three of the nine lines were given back");
        });
    }

    [AvaloniaTest]
    public void ScrollUp_StopsFollowingTheTail_AndFollowTailReturnsToIt()
    {
        WithTerminal((window, view) =>
        {
            view.IsFollowingTail.Should().BeTrue("a view at the tail follows new output");

            Wheel(window, 5);
            HeadlessUi.Pump(window);
            LinesAboveTail(view).Should().Be(15);
            view.IsFollowingTail.Should().BeFalse("scrolling back off the tail stops the follow");

            // FollowTail is what the key and text-input paths call: typing puts the user back at the prompt.
            view.FollowTail();

            LinesAboveTail(view).Should().Be(0);
            view.IsFollowingTail.Should().BeTrue();
        });
    }

    [AvaloniaTest]
    public void TrimmedScrollback_KeepsTheParkedViewOverTheSameContent()
    {
        WithTerminal((window, view) =>
        {
            var terminal = view.Terminal;

            Wheel(window, 4);                       // park 12 lines up the scrollback
            HeadlessUi.Pump(window);

            var parked = view.ViewportY;
            var topRowBefore = RowText(terminal, parked);
            topRowBefore.Should().StartWith("line ", "the view is parked over earlier output, not the tail");

            // Enough output to push the ring past capacity and start evicting — but well short of evicting
            // the parked rows themselves, which would legitimately carry the view away with them.
            var capacity = terminal.Options.Scrollback + terminal.Rows;
            var flood = capacity - terminal.Buffer.Length + 100;
            flood.Should().BeGreaterThan(0);
            for (var i = 0; i < flood; i++)
                terminal.WriteLine($"flood {i}");

            view.ViewportY.Should().BeLessThan(parked, "the ring dropped lines off the top");
            RowText(terminal, view.ViewportY).Should().Be(topRowBefore,
                "the trim handler moves the viewport down by whatever the ring dropped off the top");
        });
    }

    [AvaloniaTest]
    public void AutoScrollToBottom_False_MakesFollowTailANoOp()
    {
        WithTerminal((window, view) =>
        {
            view.AutoScrollToBottom = false;
            HeadlessUi.Pump(window);

            // Scrolling by hand still works — the property governs whether OUTPUT drags the view, and
            // whether the return-to-prompt shortcut applies, not whether the wheel moves the viewport.
            Wheel(window, 5);
            HeadlessUi.Pump(window);
            LinesAboveTail(view).Should().Be(15);
            view.IsFollowingTail.Should().BeFalse("auto-scroll is off, so the view never counts as following");

            view.FollowTail();
            LinesAboveTail(view).Should().Be(15, "FollowTail is a no-op while auto-scroll is off");
            view.IsFollowingTail.Should().BeFalse();
        });
    }

    private static string RowText(XTerm.Terminal terminal, int y)
    {
        var line = terminal.Buffer.GetLine(y);
        if (line is null) return "";
        var text = new System.Text.StringBuilder();
        for (var x = 0; x < terminal.Cols; x++) text.Append(line[x].Content);
        return text.ToString().TrimEnd();
    }
}
