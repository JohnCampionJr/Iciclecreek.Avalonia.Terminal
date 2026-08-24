using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Shift + navigation extends a selection in the buffer instead of sending the modified-cursor sequence.
///
/// <para>No interactive shell binds ESC[1;2C, so what the emulator would otherwise send comes back as the
/// literal text ";2C" in the command line — the same failure as the word-motion keys, one modifier over.</para>
/// </summary>
[TestFixture]
public class ShiftSelectionTests
{
    private static (TerminalView view, RecordingConnection pty, Window window) LiveView()
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        var pty = new RecordingConnection();
        view.AttachConnection(pty);
        view.Focus();
        Assert.That(view.IsFocused, Is.True, "sanity: OnKeyDown returns early without focus");
        return (view, pty, window);
    }

    private static void Press(TerminalView v, Key k, KeyModifiers m = KeyModifiers.None)
        => v.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = k, KeyModifiers = m });

    /// <summary>
    /// One Shift+Right selects exactly ONE cell. That is the whole reason anchor and focus are caret
    /// boundaries rather than cell indices — counting cells makes the first press select two.
    /// </summary>
    [AvaloniaTest]
    public async Task Shift_right_selects_exactly_one_cell()
    {
        var (view, pty, window) = LiveView();

        Press(view, Key.Right, KeyModifiers.Shift);
        await Task.Delay(60);

        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "a selection started");
        Assert.That(pty.Written, Is.Empty, "and nothing was sent to the shell");
        Assert.That(view.Terminal.Selection.GetSelectionText()?.Length ?? 0, Is.EqualTo(1),
            "exactly one cell, not two — the reason anchor and focus are caret boundaries");

        window.Close();
    }

    /// <summary>Collapsing back onto the anchor clears the selection, the way an editor does.</summary>
    [AvaloniaTest]
    public async Task Collapsing_back_onto_the_anchor_clears_it()
    {
        var (view, pty, window) = LiveView();

        Press(view, Key.Right, KeyModifiers.Shift);
        await Task.Delay(40);
        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity: something is selected");

        Press(view, Key.Left, KeyModifiers.Shift);
        await Task.Delay(40);

        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "back at the anchor means no selection");
        window.Close();
    }

    /// <summary>
    /// The point of the change: these keys must not reach the shell. Previously each sent a modified-cursor
    /// sequence that no default keymap binds, so zsh echoed its tail into the command line.
    /// </summary>
    [TestCase(Key.Left)]
    [TestCase(Key.Right)]
    [TestCase(Key.Up)]
    [TestCase(Key.Down)]
    [TestCase(Key.Home)]
    [TestCase(Key.End)]
    [AvaloniaTest]
    public async Task Shift_navigation_is_not_sent_to_the_shell(Key key)
    {
        var (view, pty, window) = LiveView();
        Press(view, key, KeyModifiers.Shift);
        await Task.Delay(60);
        Assert.That(pty.Written, Is.Empty, $"Shift+{key} extends a selection; it is not shell input");
        window.Close();
    }

    /// <summary>
    /// Left alone in the alternate buffer: full-screen apps draw their own selection and several bind
    /// Shift+arrow themselves, so there the sequence still belongs to the app.
    /// </summary>
    [AvaloniaTest]
    public async Task The_alternate_buffer_still_gets_the_sequence()
    {
        var (view, pty, window) = LiveView();

        view.Terminal.Write("\u001b[?1049h");     // switch to the alternate buffer
        await Task.Delay(60);
        Assert.That(view.Terminal.IsAlternateBufferActive, Is.True, "sanity: in the alternate buffer");

        Press(view, Key.Right, KeyModifiers.Shift);
        await Task.Delay(60);

        Assert.That(pty.Written, Is.Not.Empty, "a full-screen app reads the real sequence itself");
        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "and no buffer selection was made");

        window.Close();
    }

    /// <summary>A plain keystroke drops the selection, and the next Shift+arrow re-anchors at the cursor.</summary>
    [AvaloniaTest]
    public async Task A_plain_keystroke_drops_the_selection()
    {
        var (view, pty, window) = LiveView();

        Press(view, Key.Right, KeyModifiers.Shift);
        await Task.Delay(40);
        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "sanity");

        Press(view, Key.A);
        await Task.Delay(40);

        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "typing clears it");
        window.Close();
    }

    // ── Word-wise extension (#63) ───────────────────────────────────────────────────────────────

    /// <summary>Put known text in the buffer and leave the cursor at the end of it.</summary>
    private static void Type(TerminalView view, string text)
    {
        view.Terminal.Write(text);
    }

    /// <summary>
    /// Ctrl+Shift+Left extends the selection by a WORD, the way it does in every text field. Reported as
    /// #63: it moved to the word boundary but dropped the selection, because Control|Shift matched neither
    /// the Shift-selection gate nor the word-motion gate and fell through to the blanket selection-clear.
    /// </summary>
    [AvaloniaTest]
    public async Task Ctrl_shift_left_extends_the_selection_by_a_word()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, Key.Left, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(60);

        Assert.That(view.Terminal.Selection.HasSelection, Is.True, "the selection must survive");
        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("world"),
            "one word back from the cursor");
        Assert.That(pty.Written, Is.Empty, "and nothing reaches the shell");

        window.Close();
    }

    /// <summary>A second press keeps growing it, rather than re-anchoring.</summary>
    [AvaloniaTest]
    public async Task Repeated_ctrl_shift_left_keeps_growing_the_selection()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, Key.Left, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(40);
        Press(view, Key.Left, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(40);

        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("hello world"));
        window.Close();
    }

    /// <summary>And back the other way, collapsing as it returns to the anchor.</summary>
    [AvaloniaTest]
    public async Task Ctrl_shift_right_extends_back_toward_the_anchor()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, Key.Left, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(40);
        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("world"), "sanity");

        Press(view, Key.Right, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(40);

        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "back at the anchor clears it");
        window.Close();
    }

    /// <summary>Alt+Shift is the same gesture on macOS; it must behave identically.</summary>
    [AvaloniaTest]
    public async Task Alt_shift_left_extends_by_a_word_too()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, Key.Left, KeyModifiers.Alt | KeyModifiers.Shift);
        await Task.Delay(60);

        Assert.That(view.Terminal.Selection.GetSelectionText(), Is.EqualTo("world"));
        Assert.That(pty.Written, Is.Empty);
        window.Close();
    }
        private const string Esc = "\u001b";

    /// <summary>
    /// Option+Shift is the macOS word-selection gesture, and on that platform it is the ONLY one — Ctrl+arrow
    /// belongs to Mission Control, so Ctrl+Shift+arrow never reaches the app. Both are accepted so the same
    /// binding works everywhere.
    /// </summary>
    [TestCase(Key.Left, "world")]
    [TestCase(Key.Right, "")]
    [AvaloniaTest]
    public async Task Option_shift_is_the_mac_gesture(Key key, string expected)
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, key, KeyModifiers.Alt | KeyModifiers.Shift);
        await Task.Delay(60);

        Assert.That(view.Terminal.Selection.GetSelectionText() ?? "", Is.EqualTo(expected));
        Assert.That(pty.Written, Is.Empty, "a selection gesture is not shell input");
        window.Close();
    }

    /// <summary>
    /// Bare Option+arrow keeps meaning word-motion IN the shell, unchanged from #49. Pinned here because the
    /// selection gesture claims Option+SHIFT, one modifier away — it must not swallow this one.
    /// </summary>
    [TestCase(Key.Left, "b")]
    [TestCase(Key.Right, "f")]
    [AvaloniaTest]
    public async Task Bare_option_arrow_still_moves_the_shell_cursor(Key key, string letter)
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, key, KeyModifiers.Alt);
        await Task.Delay(60);

        Assert.That(pty.Written, Is.EqualTo(Esc + letter), "still ESC-b / ESC-f to the shell");
        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "and no selection is made");
        window.Close();
    }

    /// <summary>Same for bare Ctrl+arrow, which is the gesture on Windows and Linux.</summary>
    [TestCase(Key.Left, "b")]
    [TestCase(Key.Right, "f")]
    [AvaloniaTest]
    public async Task Bare_ctrl_arrow_still_moves_the_shell_cursor(Key key, string letter)
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, key, KeyModifiers.Control);
        await Task.Delay(60);

        Assert.That(pty.Written, Is.EqualTo(Esc + letter));
        Assert.That(view.Terminal.Selection.HasSelection, Is.False);
        window.Close();
    }

    // ── Line-edge gestures (#63 follow-up) ──────────────────────────────────────────────────────

    private static bool OnMac => System.Runtime.InteropServices.RuntimeInformation
        .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX);

    /// <summary>Shift+Home / Shift+End select to the line edge — the Windows and Linux gesture.</summary>
    [TestCase(Key.Home, "hello world")]
    [TestCase(Key.End, "")]
    [AvaloniaTest]
    public async Task Shift_home_and_end_select_to_the_line_edge(Key key, string expected)
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, key, KeyModifiers.Shift);
        await Task.Delay(60);

        Assert.That(view.Terminal.Selection.GetSelectionText() ?? "", Is.EqualTo(expected));
        Assert.That(pty.Written, Is.Empty, "a selection gesture is not shell input");
        window.Close();
    }

    /// <summary>
    /// A Mac keyboard has no Home/End, so Cmd+arrow is the platform's line-start/line-end — and until now it
    /// did nothing at all, swallowed by the Meta passthrough. It sends exactly what Home and End send.
    /// </summary>
    [TestCase(Key.Left, "[H")]
    [TestCase(Key.Right, "[F")]
    [AvaloniaTest]
    public async Task Cmd_arrow_is_the_mac_line_edge(Key key, string tail)
    {
        if (!OnMac) Assert.Ignore("Cmd+arrow is a macOS gesture");

        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, key, KeyModifiers.Meta);
        await Task.Delay(60);

        Assert.That(pty.Written, Is.EqualTo(Esc + tail), "the same sequence Home and End send");
        window.Close();
    }

    /// <summary>And with Shift held it selects to that edge, like Shift+Home / Shift+End.</summary>
    [TestCase(Key.Left, "hello world")]
    [TestCase(Key.Right, "")]
    [AvaloniaTest]
    public async Task Cmd_shift_arrow_selects_to_the_mac_line_edge(Key key, string expected)
    {
        if (!OnMac) Assert.Ignore("Cmd+Shift+arrow is a macOS gesture");

        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        Press(view, key, KeyModifiers.Meta | KeyModifiers.Shift);
        await Task.Delay(60);

        Assert.That(view.Terminal.Selection.GetSelectionText() ?? "", Is.EqualTo(expected));
        Assert.That(pty.Written, Is.Empty, "a selection gesture is not shell input");
        window.Close();
    }

    // ── Where the caret is drawn ────────────────────────────────────────────────────────────────

    /// <summary>The caret follows the selection's moving edge, as it does in every text field.</summary>
    [AvaloniaTest]
    public async Task The_caret_follows_the_selection_edge()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello world");
        await Task.Delay(60);

        var atCursor = view.CaretPosition;

        Press(view, Key.Left, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(40);

        Assert.That(view.CaretPosition, Is.Not.EqualTo(atCursor), "it moved with the selection");
        Assert.That(view.CaretPosition.Column, Is.EqualTo(atCursor.Column - "world".Length),
            "to the start of the selected word");

        window.Close();
    }

    /// <summary>
    /// A gesture can leave the anchor set having selected NOTHING — Shift+End at the end of a line. Release
    /// it anyway, or the caret stays pinned to a boundary the cursor has since moved away from, and typed
    /// characters append somewhere the caret is not. That is what was reported against the sample.
    /// </summary>
    [AvaloniaTest]
    public async Task A_gesture_that_selects_nothing_still_releases_the_caret()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello");
        await Task.Delay(60);

        Press(view, Key.End, KeyModifiers.Shift);
        await Task.Delay(40);
        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "sanity: nothing was selected");

        Press(view, Key.X);
        await Task.Delay(40);

        // The shell echoing input moves the real cursor on; the caret has to go with it.
        Type(view, "world");
        await Task.Delay(60);

        Assert.That(view.CaretPosition, Is.EqualTo((view.Terminal.Buffer.X,
                                                    view.Terminal.Buffer.YBase + view.Terminal.Buffer.Y)),
            "the caret is back on the shell's cursor, not pinned to the retired gesture");

        window.Close();
    }

    /// <summary>The same for the collapse path: un-selecting also retires the gesture.</summary>
    [AvaloniaTest]
    public async Task Collapsing_a_selection_releases_the_caret()
    {
        var (view, pty, window) = LiveView();
        Type(view, "hello");
        await Task.Delay(60);

        Press(view, Key.Left, KeyModifiers.Shift);
        await Task.Delay(40);
        Press(view, Key.Right, KeyModifiers.Shift);
        await Task.Delay(40);
        Assert.That(view.Terminal.Selection.HasSelection, Is.False, "sanity: collapsed");

        Type(view, "world");
        await Task.Delay(60);

        Assert.That(view.CaretPosition, Is.EqualTo((view.Terminal.Buffer.X,
                                                    view.Terminal.Buffer.YBase + view.Terminal.Buffer.Y)));
        window.Close();
    }
}
