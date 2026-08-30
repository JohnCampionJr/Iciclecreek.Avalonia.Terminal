using System.Runtime.InteropServices;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The defaults README.md publishes for <see cref="TerminalWindow"/>, plus the properties it inherits in
/// spirit from TerminalControl, asserted on a fresh window that has never been shown.
/// </summary>
[TestClass]
public class TerminalWindowDefaultsTests
{
    private static bool Windows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>README: CloseOnProcessExit default true.</summary>
    [AvaloniaTest]
    public void CloseOnProcessExit_defaults_to_true()
    {
        var window = new TerminalWindow();

        window.CloseOnProcessExit.Should().BeTrue($"observed {window.CloseOnProcessExit}");
    }

    /// <summary>README: UpdateTitleFromTerminal default true.</summary>
    [AvaloniaTest]
    public void UpdateTitleFromTerminal_defaults_to_true()
    {
        var window = new TerminalWindow();

        window.UpdateTitleFromTerminal.Should().BeTrue($"observed {window.UpdateTitleFromTerminal}");
    }

    /// <summary>Same platform-shell contract as TerminalControl; the two must not disagree.</summary>
    [AvaloniaTest]
    public void Process_defaults_to_the_platform_shell()
    {
        var window = new TerminalWindow();

        var expected = Windows ? "cmd.exe" : "bash";
        window.Process.Should().Be(expected, $"TerminalWindow and TerminalControl must agree on the default shell. Observed '{window.Process}'");
    }

    /// <summary>
    /// TerminalWindow already gets this right — it defaults to the current directory where TerminalControl
    /// defaults to null. Locked in so the two converge rather than the correct one regressing to match.
    /// </summary>
    [AvaloniaTest]
    public void StartingDirectory_defaults_to_the_current_directory()
    {
        var window = new TerminalWindow();

        window.StartingDirectory.Should().Be(Environment.CurrentDirectory, $"observed '{window.StartingDirectory ?? "null"}'");
    }

    /// <summary>Window properties set before Show() must survive both hops: window -> control -> view.</summary>
    [AvaloniaTest]
    public void Properties_set_before_showing_reach_the_inner_view()
    {
        var expected = Path.GetTempPath();
        var window = new TerminalWindow { Process = "", StartingDirectory = expected, FontSize = 19 }.Realise();

        try
        {
            var control = window.Control();
            var view = control.View();

            using (new AssertionScope())
        {
                control.StartingDirectory.Should().Be(expected, $"first hop (window -> control) observed '{control.StartingDirectory ?? "null"}'");
                view.StartingDirectory.Should().Be(expected, $"second hop (control -> view) observed '{view.StartingDirectory ?? "null"}'");
                view.FontSize.Should().Be(19, $"second hop (control -> view) observed {view.FontSize}");
            };
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The convenience overload documented as "sets StartingDirectory, Process, and Args, then launches".
    /// The write-through half is assertable without launching anything.
    /// </summary>
    [AvaloniaTest]
    public void LaunchProcess_overload_writes_through_to_the_properties()
    {
        var window = new TerminalWindow { Process = "" }.Realise();
        var dir = Path.GetTempPath();

        // Deliberately not awaited: the launch itself needs a real process, but the documented
        // property write-through happens before that and is what this asserts.
        _ = window.LaunchProcess(dir, "/bin/sh", "-c", "exit 0");

        try
        {
            using (new AssertionScope())
        {
                window.StartingDirectory.Should().Be(dir, $"observed '{window.StartingDirectory ?? "null"}'");
                window.Process.Should().Be("/bin/sh", $"observed '{window.Process}'");
                window.Args.Should().Equal(new[] { "-c", "exit 0" },
                    $"observed [{string.Join(", ", window.Args ?? [])}]");
            };
        }
        finally
        {
            window.Close();
        }
    }
}
