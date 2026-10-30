using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// A program asking about the window has to get an answer back.
/// </summary>
/// <remarks>
/// <para>The reply used to never arrive. The emulator raises the request synchronously and reads
/// <c>Handled</c> the moment the handler returns; the handler posted the work to the UI thread and returned
/// immediately, so <c>Handled</c> was still false and nothing was sent. The answer was computed correctly
/// and written after the only reader of it had given up.</para>
/// <para>Asserted on what reaches the pty, because that is the part a program can see. A handler that sets
/// the right values on the wrong thread looks identical from inside.</para>
/// </remarks>
[TestFixture]
public class WindowQueryTests
{
    private const string Esc = "\u001b";

    private static (TerminalView view, RecordingConnection pty, Window window) LiveView()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 900, Height = 400, Content = view };
        window.Show();
        window.UpdateLayout();

        // The reports are opt-in; a host says which it will answer.
        var options = view.Terminal.Options.WindowOptions;
        options.GetWinSizeChars = true;
        options.GetWinSizePixels = true;
        options.GetCellSizePixels = true;
        options.GetWinPosition = true;

        var pty = new RecordingConnection();
        view.AttachConnection(pty);
        return (view, pty, window);
    }

    /// <summary>Answer the request, the way a host embedding the terminal does.</summary>
    private static void AnswerWith(TerminalView view, int width, int height)
    {
        TerminalView.AddWindowInfoRequestedHandler(view, (_, e) =>
        {
            e.WidthPixels = width;
            e.HeightPixels = height;
            e.CellWidth = 8;
            e.CellHeight = 16;
            e.X = 10;
            e.Y = 20;
            e.Handled = true;
        });
    }

    [TestCase("[14t", TestName = "pixel size")]
    [TestCase("[16t", TestName = "cell size")]
    [TestCase("[13t", TestName = "window position")]
    [AvaloniaTest]
    public async Task A_window_query_is_answered(string query)
    {
        var (view, pty, window) = LiveView();
        AnswerWith(view, 640, 480);

        view.Terminal.Write(Esc + query);

        Assert.That(await PtyWaits.AwaitOutput(pty), Is.Not.Empty,
            "nothing was sent back — the handler's answer arrived after the emulator had stopped listening");
        Assert.That(pty.Written, Does.StartWith(Esc + "["), "and it should be a CSI report");

        window.Close();
    }

    /// <summary>
    /// GEOMETRY the view itself knows is answered even with no handler: cell and text-area sizes
    /// are the view's own numbers, and silence there makes an image client fall back to guessing
    /// a cell size the emulator's tiling then disagrees with — which shears every placeholder
    /// picture into repeated bands. The answer must be the same metric the emulator tiles with.
    /// </summary>
    [AvaloniaTest]
    public async Task A_geometry_query_is_answered_even_with_no_handler()
    {
        var (view, pty, window) = LiveView();

        view.Terminal.Write(Esc + "[16t");

        Assert.That(await PtyWaits.AwaitOutput(pty), Is.Not.Empty,
            "the view knows its own cell size and must say so");
        Assert.That(pty.Written, Does.StartWith(Esc + "[6;"), "CSI 6 ; height ; width t");

        window.Close();
    }

    /// <summary>
    /// What the view CANNOT know on its own — where the window sits on the screen — is answered
    /// from the position winop 3 last set, which is 0;0 until something sets it.
    /// </summary>
    /// <remarks>
    /// This used to assert silence, on the reasoning that inventing an answer is worse than saying
    /// nothing. The emulator settled it the other way in its esctest sweep, because xterm answers
    /// and silence is not free: a client that blocks waiting for CSI 3 t hangs on a terminal that
    /// never replies. So the report is opt-in — <c>GetWinPosition</c>, which this fixture turns on
    /// — and a host that knows better answers it, as the next test does.
    /// </remarks>
    [AvaloniaTest]
    public async Task An_unhandled_position_query_falls_back_to_the_last_position_set()
    {
        var (view, pty, window) = LiveView();

        view.Terminal.Write(Esc + "[13t");

        Assert.That(await PtyWaits.AwaitOutput(pty), Is.EqualTo(Esc + "[3;0;0t"),
            "CSI 3 ; x ; y t, and nothing has set a position");

        window.Close();
    }

    /// <summary>
    /// For facts only the host can know -- where the window sits -- the values the host supplied
    /// are the ones that go out. Pixel geometry no longer consults handlers at all: those answers
    /// come inline from the view's own published metrics, on the reader's thread, because waiting
    /// on the UI thread for a number the view already knows stalled the reader for up to a quarter
    /// of a second whenever the UI was busy.
    /// </summary>
    [AvaloniaTest]
    public async Task The_answer_carries_what_the_host_said()
    {
        var (view, pty, window) = LiveView();
        AnswerWith(view, 640, 480);

        view.Terminal.Write(Esc + "[13t");

        Assert.That(await PtyWaits.AwaitOutput(pty), Does.Contain("20").And.Contain("10"),
            "CSI 3 ; x ; y t with the handler's coordinates");

        window.Close();
    }

    /// <summary>
    /// Pixel geometry is answered on the READER's thread from the published metrics -- no UI-thread
    /// round trip, no quarter-second stall when the UI is busy, and the same numbers the emulator
    /// tiles images by.
    /// </summary>
    [AvaloniaTest]
    public async Task Pixel_geometry_reports_the_views_own_metrics_not_a_handlers()
    {
        var (view, pty, window) = LiveView();
        AnswerWith(view, 640, 480);   // a handler claiming other numbers is not consulted

        view.Terminal.Write(Esc + "[16t");

        var written = await PtyWaits.AwaitOutput(pty);
        Assert.That(written, Does.StartWith(Esc + "[6;"));
        Assert.That(written, Does.Not.Contain("640").And.Not.Contain("480"),
            "the view's real cell size goes out, not the handler's fiction");

        window.Close();
    }

    // ---- Through the read loop ----
    //
    // The tests above write on the UI thread, where the view asks its handlers inline. A real program's
    // queries arrive on the pty READER thread, inside _terminalLock, where waiting on the UI thread is
    // what froze it. These answers come from what the UI thread last published.

    /// <summary>
    /// A view in a window whose host answers the way TerminalWindow does: from the window itself. The
    /// handler goes on the WINDOW, after the view is attached -- a host's handler sits above the view.
    /// </summary>
    private static (TerminalView view, RecordingConnection pty, PushStream output, Window window) ReaderView()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 900, Height = 400, Content = view };
        window.Show();
        window.UpdateLayout();

        TerminalView.AddWindowInfoRequestedHandler(window, (_, e) =>
        {
            e.IsIconified = window.WindowState == WindowState.Minimized;
            e.X = window.Position.X;
            e.Y = window.Position.Y;
            e.Title = window.Title;
            e.Handled = true;
        });

        var output = new PushStream();
        var pty = new RecordingConnection(output);
        view.AttachConnection(pty);
        return (view, pty, output, window);
    }

    [AvaloniaTest]
    public async Task Position_is_where_the_window_last_moved_to()
    {
        var (view, pty, output, window) = ReaderView();
        view.Terminal.Options.WindowOptions.GetWinPosition = true;

        window.Position = new PixelPoint(120, 45);
        output.Push(Esc + "[13t");

        Assert.That(await PtyWaits.AwaitOutput(pty), Is.EqualTo(Esc + "[3;120;45t"));
        window.Close();
    }

    /// <summary>A handler added after the view is attached is asked, with nothing else changing.</summary>
    [AvaloniaTest]
    public async Task A_handler_added_later_is_the_one_answering()
    {
        var (view, pty, output, window) = ReaderView();
        view.Terminal.Options.WindowOptions.GetWinPosition = true;

        TerminalView.AddWindowInfoRequestedHandler(view, (_, e) =>
        {
            e.X = 10;
            e.Y = 20;
            e.Handled = true;
        });
        output.Push(Esc + "[13t");

        Assert.That(await PtyWaits.AwaitOutput(pty), Is.EqualTo(Esc + "[3;10;20t"));
        window.Close();
    }

    [AvaloniaTest]
    public async Task State_is_what_the_window_last_became()
    {
        var (view, pty, output, window) = ReaderView();
        view.Terminal.Options.WindowOptions.GetWinState = true;

        output.Push(Esc + "[11t");
        Assert.That(await PtyWaits.AwaitOutput(pty), Is.EqualTo(Esc + "[1t"), "1 = not iconified");

        window.WindowState = WindowState.Minimized;
        output.Push(Esc + "[11t");
        Assert.That(await PtyWaits.AwaitOutput(pty, 4), Is.EqualTo(Esc + "[1t" + Esc + "[2t"), "2 = iconified");
        window.Close();
    }

    [AvaloniaTest]
    public async Task Icon_title_is_what_the_window_was_last_titled()
    {
        var (view, pty, output, window) = ReaderView();
        view.Terminal.Options.WindowOptions.GetIconTitle = true;

        window.Title = "build";
        output.Push(Esc + "[20t");

        Assert.That(await PtyWaits.AwaitOutput(pty), Is.EqualTo(Esc + "]Lbuild" + Esc + "\\"));
        window.Close();
    }

    [AvaloniaTest]
    public async Task Size_in_characters_is_the_grid()
    {
        var (view, pty, output, window) = ReaderView();

        output.Push(Esc + "[18t");

        Assert.That(await PtyWaits.AwaitOutput(pty),
            Is.EqualTo($"{Esc}[8;{view.Terminal.Rows};{view.Terminal.Cols}t"));
        window.Close();
    }

    [AvaloniaTest]
    public async Task Window_title_is_the_one_the_program_set()
    {
        var (view, pty, output, window) = ReaderView();
        view.Terminal.Options.WindowOptions.GetWinTitle = true;

        output.Push(Esc + "]2;hello\a" + Esc + "[21t");

        Assert.That(await PtyWaits.AwaitOutput(pty), Is.EqualTo(Esc + "]lhello" + Esc + "\\"));
        window.Close();
    }

    /// <summary>
    /// A change and the query about it in ONE chunk, through a real TerminalWindow at its defaults --
    /// which turns the window queries on. The query arrives before the posted change reaches the
    /// window, so the answer published earlier is stale; the reply must still carry the new value.
    /// </summary>
    [TestCase("]0;new\a", "[20t", "]Lnew\u001b\\", false, TestName = "retitle, then ask the icon title")]
    [TestCase("[2t", "[11t", "[2t", true, TestName = "iconify, then ask the state")]
    [AvaloniaTest]
    public async Task A_query_after_a_change_in_the_same_chunk_sees_the_change(
        string change, string query, string reply, bool allowWindowOps)
    {
        var window = new TerminalWindow { Process = "", AllowWindowOps = allowWindowOps }.Realise();
        try
        {
            var output = new PushStream();
            var pty = new RecordingConnection(output);
            window.AttachConnection(pty);

            output.Push(Esc + change + Esc + query);

            Assert.That(await PtyWaits.AwaitOutput(pty), Is.EqualTo(Esc + reply));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A host that answers from its OWN state -- a tab label kept up to date from TitleChanged, not
    /// the window's Title -- changes nothing the view watches. The answer is re-published after the
    /// change anyway, so the next query has it.
    /// </summary>
    [AvaloniaTest]
    public async Task A_host_answering_from_its_own_state_is_current_after_the_change()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 900, Height = 400, Content = view };
        window.Show();
        window.UpdateLayout();
        try
        {
            var label = "old";
            TerminalView.AddTitleChangedHandler(view, (_, e) => label = e.Title);
            TerminalView.AddWindowInfoRequestedHandler(view, (_, e) => { e.Title = label; e.Handled = true; });
            view.Terminal.Options.WindowOptions.GetIconTitle = true;

            var output = new PushStream();
            var pty = new RecordingConnection(output);
            view.AttachConnection(pty);

            output.Push(Esc + "]0;new\a");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (label != "new" && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.That(label, Is.EqualTo("new"), "the host saw the title change");

            output.Push(Esc + "[20t");
            Assert.That(await PtyWaits.AwaitOutput(pty), Is.EqualTo(Esc + "]Lnew" + Esc + "\\"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The gates are the emulator's and keep their defaults: position, state and both titles are off
    /// until a host turns them on, size in characters is on. A device-status query closes the chunk
    /// so "nothing else was sent" is a fact about the reply stream, not a timeout.
    /// </summary>
    [TestCase(false, TestName = "gates at their defaults")]
    [TestCase(true, TestName = "gates on")]
    [AvaloniaTest]
    public async Task The_gates_decide_what_is_answered(bool gatesOn)
    {
        var (view, pty, output, window) = ReaderView();
        window.Position = new PixelPoint(7, 9);
        window.Title = "t";
        var gates = view.Terminal.Options.WindowOptions;
        if (gates.GetWinPosition || gates.GetWinState || gates.GetIconTitle || gates.GetWinTitle || !gates.GetWinSizeChars)
            Assert.Fail("the defaults moved");
        if (gatesOn)
            gates.GetWinPosition = gates.GetWinState = gates.GetIconTitle = gates.GetWinTitle = true;

        output.Push(Esc + "]2;t\a" + Esc + "[11t" + Esc + "[13t" + Esc + "[18t" + Esc + "[20t" + Esc + "[21t" + Esc + "[5n");

        var size = $"{Esc}[8;{view.Terminal.Rows};{view.Terminal.Cols}t";
        var expected = gatesOn
            ? Esc + "[1t" + Esc + "[3;7;9t" + size + Esc + "]Lt" + Esc + "\\" + Esc + "]lt" + Esc + "\\" + Esc + "[0n"
            : size + Esc + "[0n";
        Assert.That(await PtyWaits.AwaitOutput(pty), Is.EqualTo(expected));
        window.Close();
    }
}
