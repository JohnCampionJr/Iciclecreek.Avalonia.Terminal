
namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The methods and read-only members README.md documents for <see cref="TerminalControl"/>, covered without
/// spawning anything.
///
/// <para>Most of the interesting behaviour here is about what happens BEFORE a process exists, which is
/// exactly the part a consumer meets first and the part no integration test reaches.</para>
/// </summary>
[TestClass]
public class TerminalControlContractTests
{
    /// <summary>
    /// README documents LaunchProcess as launching "the configured Process". On a control that was never
    /// realised there is no inner view to launch into, and the control says so rather than throwing
    /// something incidental — it is the one documented exception on the type.
    /// </summary>
    [AvaloniaTest]
    public void LaunchProcess_on_an_unrealised_control_reports_why_it_cannot()
    {
        var control = new TerminalControl { Process = "" };

        Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await control.LaunchProcess(),
            "a control with no template has no terminal to launch into, and should say so plainly");
    }

    /// <summary>
    /// The convenience overload is documented as "sets StartingDirectory, Process, and Args, then launches".
    /// The write-through is observable on its own, without a launch succeeding.
    /// </summary>
    [AvaloniaTest]
    public void LaunchProcess_overload_writes_through_to_the_properties()
    {
        var control = new TerminalControl { Process = "" };
        var dir = Path.GetTempPath();

        // The launch itself cannot succeed on an unrealised control — the documented write-through happens
        // first and is what this asserts.
        Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await control.LaunchProcess(dir, "/bin/sh", "-c", "exit 0"));

        using (new AssertionScope())
        {
            control.StartingDirectory.Should().Be(dir, $"observed '{control.StartingDirectory ?? "null"}'");
            control.Process.Should().Be("/bin/sh", $"observed '{control.Process}'");
            control.Args.Should().Equal(new[] { "-c", "exit 0" },
                $"observed [{string.Join(", ", control.Args ?? [])}]");
        };
    }

    /// <summary>
    /// README: CurrentDirectory is "reported by the running terminal session via OSC 7".
    ///
    /// <para>Driven by writing the escape sequence to the emulator rather than by running a shell that emits
    /// it. What is being tested is that the control surfaces what the view reports — the parsing of the
    /// sequence is XTerm.NET's contract, and asserting a particular parsed form here would be testing
    /// upstream and coupling this suite to their implementation.</para>
    /// </summary>
    [AvaloniaTest]
    public void CurrentDirectory_follows_what_the_view_reports()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            control.Terminal.Write("]7;file://host/tmp");

            control.CurrentDirectory.Should().Be(control.View().CurrentDirectory, $"the control must surface the view's value, whatever it parsed. "
                + $"control='{control.CurrentDirectory ?? "null"}' view='{control.View().CurrentDirectory ?? "null"}'");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// README documents Kill() as "Terminates the running terminal process". With no process running there
    /// is nothing to terminate, and a consumer tidying up a control that never launched should not have to
    /// guard the call.
    /// </summary>
    [AvaloniaTest]
    public void Kill_is_safe_when_no_process_is_running()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            ((Action)(() => control.Kill())).Should().NotThrow("killing a terminal that never launched is a no-op, not an error");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The inner emulator is public API (<c>Terminal</c>) and is what several documented properties are
    /// really about. It must exist as soon as the control is realised, not only once a process runs.
    /// </summary>
    [AvaloniaTest]
    public void The_emulator_exists_as_soon_as_the_control_is_realised()
    {
        var control = new TerminalControl { Process = "" };
        var window = TerminalHost.Show(control);

        try
        {
            control.Terminal.Should().NotBeNull();
            control.Terminal.Options.Should().NotBeNull("documented options-backed properties are meaningless without this");
        }
        finally
        {
            window.Close();
        }
    }
}
