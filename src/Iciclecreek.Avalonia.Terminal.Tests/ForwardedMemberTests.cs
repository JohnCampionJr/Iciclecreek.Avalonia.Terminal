using Avalonia.Media;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// That the forwarded members actually work, rather than merely existing.
///
/// <para><see cref="SurfaceParityTests"/> proves the surface is complete by walking it with reflection —
/// but a member that exists and returns nonsense passes that test perfectly. These assert the values go
/// where they are supposed to, which is the half reflection cannot see.</para>
/// </summary>
[TestClass]
public class ForwardedMemberTests
{
    // ---- cursor appearance: real properties, so a value set before realisation must survive ----------

    [AvaloniaTest]
    public void Cursor_appearance_set_before_realisation_reaches_the_view()
    {
        var control = new TerminalControl
        {
            Process = "",
            CursorColor = Colors.Magenta,
            CursorStyle = XTerm.Common.CursorStyle.Block,
            CursorBlink = false,
            CursorBlinkRate = 250,
        };
        var host = TerminalHost.Show(control);

        try
        {
            var view = control.View();
            using (new AssertionScope())
        {
                view.CursorColor.Should().Be(Colors.Magenta, $"observed {view.CursorColor}");
                view.CursorStyle.Should().Be(XTerm.Common.CursorStyle.Block, $"observed {view.CursorStyle}");
                view.CursorBlink.Should().BeFalse($"observed {view.CursorBlink}");
                view.CursorBlinkRate.Should().Be(250, $"observed {view.CursorBlinkRate}");
            };
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>And through both hops from the window.</summary>
    [AvaloniaTest]
    public void Cursor_appearance_set_on_a_window_reaches_the_view()
    {
        var window = new TerminalWindow
        {
            Process = "",
            CursorColor = Colors.Lime,
            CursorStyle = XTerm.Common.CursorStyle.Underline,
        }.Realise();

        try
        {
            var view = window.Control().View();
            using (new AssertionScope())
        {
                view.CursorColor.Should().Be(Colors.Lime, $"observed {view.CursorColor}");
                view.CursorStyle.Should().Be(XTerm.Common.CursorStyle.Underline, $"observed {view.CursorStyle}");
            };
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// TextDecorations was registered from the start with no CLR property and no template binding — settable
    /// from XAML, stored, and read by nothing.
    /// </summary>
    [AvaloniaTest]
    public void TextDecorations_set_before_realisation_reaches_the_view()
    {
        var control = new TerminalControl { Process = "", TextDecorations = TextDecorationLocation.Underline };
        var host = TerminalHost.Show(control);

        try
        {
            (control.View().TextDecorations).Should().Be(TextDecorationLocation.Underline, $"observed {control.View().TextDecorations?.ToString() ?? "null"}");
        }
        finally
        {
            host.Close();
        }
    }

    // ---- viewport state: live values, so these forward rather than store ----------------------------

    [AvaloniaTest]
    public void Viewport_state_reports_the_views_values()
    {
        var control = new TerminalControl { Process = "" };
        var host = TerminalHost.Show(control);

        try
        {
            var view = control.View();
            using (new AssertionScope())
        {
                control.ViewportLines.Should().Be(view.ViewportLines,
                    $"control={control.ViewportLines} view={view.ViewportLines}");
                control.ViewportLines.Should().BeGreaterThan(0);
                control.MaxScrollback.Should().Be(view.MaxScrollback);
                control.ViewportY.Should().Be(view.ViewportY);
                control.IsAlternateBuffer.Should().Be(view.IsAlternateBuffer);
            };
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>Writing ViewportY has to move the view, or a host cannot drive its own scrollbar.</summary>
    [AvaloniaTest]
    public void Setting_ViewportY_moves_the_view()
    {
        var control = new TerminalControl { Process = "" };
        var host = TerminalHost.Show(control);

        try
        {
            // Fill the buffer so there is somewhere to scroll to.
            for (var i = 0; i < 200; i++)
                control.Terminal.Write($"line {i}\r\n");

            control.MaxScrollback.Should().BeGreaterThan(0, "needs a scrollback to test against");

            control.ViewportY = 5;

            (control.View().ViewportY).Should().Be(5, $"observed {control.View().ViewportY}");
        }
        finally
        {
            host.Close();
        }
    }

    // ---- the no-op contracts before realisation ----------------------------------------------------

    /// <summary>
    /// Every forwarder that can sensibly do nothing does nothing, rather than throwing, on a control whose
    /// template has not been applied. This is the shape of bug ExitCode and Pid had.
    /// </summary>
    [AvaloniaTest]
    public void Forwarders_are_safe_before_the_template_is_applied()
    {
        var control = new TerminalControl { Process = "" };

        using (new AssertionScope())
        {
            control.ViewportY.Should().Be(0);
            control.MaxScrollback.Should().Be(0);
            control.ViewportLines.Should().Be(0);
            control.IsAlternateBuffer.Should().BeFalse();
            control.IsLive.Should().BeFalse();
            ((Action)(() => control.ViewportY = 3)).Should().NotThrow();
            ((Action)(() => control.DetachConnection())).Should().NotThrow();
            ((Func<Task>)(async () => await control.PasteAsync())).Should().NotThrowAsync().GetAwaiter().GetResult();
            ((Func<Task>)(async () => await control.CopyAsync())).Should().NotThrowAsync().GetAwaiter().GetResult();
        };
    }

    /// <summary>The same from an unshown window, which is two forwarders deep.</summary>
    [AvaloniaTest]
    public void Window_forwarders_are_safe_before_it_is_shown()
    {
        var window = new TerminalWindow { Process = "" };

        using (new AssertionScope())
        {
            window.ViewportLines.Should().Be(0);
            window.IsLive.Should().BeFalse();
            ((Action)(() => window.DetachConnection())).Should().NotThrow();
            ((Func<Task>)(async () => await window.CopyAsync())).Should().NotThrowAsync().GetAwaiter().GetResult();
        };
    }

    /// <summary>
    /// CopyAsync reports false when there is no selection, rather than throwing — the return value is the
    /// signal, so a host can decide whether to fall back to something else.
    /// </summary>
    [AvaloniaTest]
    public async Task CopyAsync_reports_false_with_nothing_selected()
    {
        var control = new TerminalControl { Process = "" };
        var host = TerminalHost.Show(control);

        try
        {
            (await control.CopyAsync()).Should().BeFalse("nothing was selected");
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>
    /// Attaching is the one forwarder that refuses to fail quietly: handing over a live PTY and having it
    /// ignored would leave the caller believing a process is on screen when it is not.
    /// </summary>
    [AvaloniaTest]
    public void Attaching_to_an_unrealised_control_reports_why_it_cannot()
    {
        var control = new TerminalControl { Process = "" };

        Assert.ThrowsExactly<InvalidOperationException>(() => control.AttachConnection(null!),
            "an unrealised control has no terminal to attach to, and should say so rather than no-op");
    }
}
