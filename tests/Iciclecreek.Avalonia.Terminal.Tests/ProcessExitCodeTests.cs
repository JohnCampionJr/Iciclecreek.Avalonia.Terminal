using Porta.Pty;
using Avalonia.Controls;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// What <see cref="TerminalView.ProcessExited"/> reports has to be what the process actually
/// returned. It is the only thing a host has to go on — the buffer's own "Process exited with
/// code" line is frequently cleared by the host on the way back to an idle state — so a wrong code
/// here becomes a UI that tells the user their build succeeded when it failed.
///
/// <para>Repeated deliberately. There are two paths that can report an exit, racing behind one
/// interlock, and which of them wins depends on scheduling — so a single pass proves nothing. The
/// loop is what makes the assertion mean "always" rather than "this time".</para>
/// </summary>
[TestClass]
public class ProcessExitCodeTests
{
    // Asserts on the codes that were REPORTED, not on how many arrived in the window. Spawning a
    // batch of PTYs on a two-core runner starves some of them — measured, 7 of 32 never reported
    // inside 10s on ubuntu-latest, while every code that DID arrive was correct. Failing on that
    // would be reporting the runner's throughput as a product defect, and it is the reason a first
    // version of this test went red on CI with the fix already in place.
    //
    // Contention is what surfaces the race — it first appeared in a full-assembly run, not alone.
    // Even so this reproduction is STATISTICAL: measured against the reverted fix, 20 sequential
    // spawns caught it 0 times in 3 runs and these concurrent batches caught it 2 times in 4. That
    // is why the deterministic test below exists as well; treat this one as the integration check
    // that the whole real path behaves, not as the guard.
    private const int Rounds = 2;
    private const int Concurrent = 6;
    private static readonly TimeSpan ReportWindow = TimeSpan.FromSeconds(30);

    private static bool Posix => !OperatingSystem.IsWindows();

    /// <summary>Every exit code reported across all rounds, in completion order.</summary>
    private static async Task<List<int?>> ReportedExitCodes(int code)
    {
        var seen = new List<int?>();
        for (var round = 0; round < Rounds; round++)
        {
            var batch = Enumerable.Range(0, Concurrent).Select(_ => ReportedExitCode(code)).ToArray();
            seen.AddRange(await Task.WhenAll(batch));
        }
        return seen;
    }

    /// <summary>Run a shell that exits with <paramref name="code"/> and return what the view
    /// reported — or null if it never reported at all.</summary>
    private static async Task<int?> ReportedExitCode(int code)
    {
        var view = new TerminalView { Process = "" };
        var window = HeadlessUi.Show(view);
        HeadlessUi.Pump(window);

        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.ProcessExited += (_, e) => exited.TrySetResult(e.ExitCode);

        await view.LaunchProcess(Path.GetTempPath(), "/bin/sh", "-c", $"exit {code}");

        var done = await Task.WhenAny(exited.Task, Task.Delay(ReportWindow));
        window.Close();
        return done == exited.Task ? exited.Task.Result : null;
    }

    [TestMethod]
    public void A_nonzero_exit_code_is_reported_faithfully()
    {
        if (!Posix) Assert.Inconclusive("POSIX only");
        HeadlessUi.RunAsync(async () =>
        {
            var seen = await ReportedExitCodes(3);
            var detail = " Saw: " + string.Join(",", seen.Select(c => c?.ToString() ?? "none"));

            seen.Should().Contain(c => c.HasValue, "the exit path has to work at all." + detail);
            seen.Where(c => c.HasValue).Should().OnlyContain(c => c == 3,
                "a reported code that is not 3 is a host being told the wrong outcome." + detail);
        });
    }

    [TestMethod]
    public void A_clean_exit_is_reported_as_zero()
    {
        if (!Posix) Assert.Inconclusive("POSIX only");
        HeadlessUi.RunAsync(async () =>
        {
            var seen = await ReportedExitCodes(0);
            var detail = " Saw: " + string.Join(",", seen.Select(c => c?.ToString() ?? "none"));

            seen.Should().Contain(c => c.HasValue, "the exit path has to work at all." + detail);
            seen.Where(c => c.HasValue).Should().OnlyContain(c => c == 0, detail);
        });
    }

    // ── The deterministic guard ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A connection whose child has EXITED but not yet been REAPED — which is precisely the window
    /// the bug lived in. Its reader stream is already at EOF, its ProcessExited never fires (so the
    /// EOF path in the view is forced to be the one that reports), and its ExitCode is the default
    /// 0 until <see cref="WaitForExit"/> is called, exactly as a real connection behaves.
    ///
    /// <para>Racing a real shell only reproduces this half the time. Modelling the window directly
    /// turns "we got unlucky enough to see it" into a guard that cannot pass against the bug.</para>
    /// </summary>
    private sealed class ExitedButNotYetReaped : IPtyConnection
    {
        private readonly int _realExitCode;
        private readonly bool _everReaps;
        private readonly int _reapsOnCall;
        private int _waitCalls;
        private bool _reaped;

        /// <param name="reapsOnCall">
        /// Which <see cref="WaitForExit"/> call finally succeeds, 1-based. 1 is the ordinary case —
        /// the child is already dead and reaps immediately. A HIGHER value models the pathological
        /// one this class exists for: the read loop's grace period expires without a reap, and the
        /// child reaps a moment later. That is not hypothetical — it is what a CI box under enough
        /// load does, and it is the case that used to end in no exit event at all.
        /// </param>
        public ExitedButNotYetReaped(int realExitCode, bool everReaps = true, int reapsOnCall = 1)
        {
            _realExitCode = realExitCode;
            _everReaps = everReaps;
            _reapsOnCall = reapsOnCall;
        }


        public bool WasWaitedOn { get; private set; }

        /// <summary>0 until reaped — the whole point. Reading this too early is the defect.</summary>
        public int ExitCode => _reaped ? _realExitCode : 0;

        public bool WaitForExit(int milliseconds)
        {
            WasWaitedOn = true;
            _waitCalls++;
            _reaped = _everReaps && _waitCalls >= _reapsOnCall;
            return _reaped;
        }

        // An empty stream reads 0 bytes immediately, which is EOF.
        public Stream ReaderStream { get; } = new MemoryStream(Array.Empty<byte>());
        public Stream WriterStream { get; } = new MemoryStream();

        public int Pid => -1;
        public void Kill() { }
        public void Resize(int columns, int rows) { }
        public void Dispose() { }

        /// <summary>Never raised: EOF alone has to drive these cases, which is the point.</summary>
        public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }
    }

    /// <summary>
    /// The contract, stated without a race: when the read loop sees EOF it must report what the
    /// process ACTUALLY returned, which means not reading the exit code until the child has been
    /// reaped. Before the fix this reported 0 for a process that returned 3, every time.
    /// </summary>
    [TestMethod]
    public void An_exit_seen_only_as_EOF_still_reports_the_real_code() => HeadlessUi.RunAsync(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = HeadlessUi.Show(view);
        HeadlessUi.Pump(window);

        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.ProcessExited += (_, e) => exited.TrySetResult(e.ExitCode);

        var connection = new ExitedButNotYetReaped(realExitCode: 3);
        view.AttachConnection(connection);

        var done = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        done.Should().BeSameAs(exited.Task, "EOF alone has to be enough to report an exit");
        exited.Task.Result.Should().Be(3,
            "reading ExitCode before the child is reaped reports 0 for a process that failed");
        connection.WasWaitedOn.Should().BeTrue("the reap is what makes the code readable");

        window.Close();
    });

    /// <summary>
    /// A child that will not reap inside the grace period leaves no trustworthy exit code — and
    /// the one that would be read is 0, the single wrong answer that reads as SUCCESS. Rather than
    /// invent an outcome, the EOF path leaves the exit interlock unclaimed, so the real event can
    /// still report if it ever arrives.
    ///
    /// <para>This is the pathological branch; the ordinary one reaps immediately. It is covered
    /// because the alternative — claiming on a failed reap — silently reasserts the very bug this
    /// change exists to fix, and does so in the case nobody would think to try by hand.</para>
    ///
    /// <para>NOTE the deferral is BOUNDED now, not permanent. Leaving it permanent meant a child
    /// that never reaped produced no ProcessExited at all, and a host that is never told the
    /// process ended cannot leave the state it entered when it started — a host's session view sat
    /// in Live forever. The exit is reported once the ceiling expires, with ExitCodeKnown false.
    /// This test asserts the first 200ms, which is the part that must not change: the authoritative
    /// event still gets its window.</para>
    /// </summary>
    [TestMethod]
    public void A_child_that_will_not_reap_defers_to_the_real_event() => HeadlessUi.RunAsync(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = HeadlessUi.Show(view);
        HeadlessUi.Pump(window);

        var reported = new List<int>();
        view.ProcessExited += (_, e) => reported.Add(e.ExitCode);

        var connection = new ExitedButNotYetReaped(realExitCode: 3, everReaps: false);
        view.AttachConnection(connection);

        // EOF has been seen and the reap refused. Nothing may be reported off the back of that.
        await HeadlessUi.WaitUntil(() => connection.WasWaitedOn, "the EOF path tried to reap");
        await Task.Delay(200);
        reported.Should().BeEmpty("0 would be invented, and 0 is the answer that reads as success");

        // …and the interlock is still free, which is what leaves the authoritative event able to
        // speak. IsLive is exactly that flag (_ptyConnection != null && _processExitHandled == 0),
        // so it is the observable form of "nothing has claimed the exit yet" — asserted through the
        // public surface rather than by synthesising a PtyExitedEventArgs, whose constructor the
        // PTY library does not expose.
        view.IsLive.Should().BeTrue("a failed reap must not claim the exit and lock the real event out");

        window.Close();
    });

    /// <summary>
    /// A child that misses the read loop's grace period but reaps a moment later must still be
    /// reported, with its real code.
    ///
    /// <para>This is the guard for the wedge. The EOF path used to give up when the grace period
    /// expired: the interlock stayed unclaimed, and if the PTY layer's own event never fired either
    /// — which this fake models, and which the layer genuinely does — then NO ProcessExited was
    /// raised at all. The trade recorded at the time was "no wrong exit code beats a wrong one",
    /// but the cost was not the number, it was the notification. A host's session view stayed in
    /// TerminalPhase.Live forever and every test waiting for it to settle timed out at 20s. It
    /// surfaced as a load-sensitive CI flake, because a dead child only misses a 1000ms reap when
    /// the box is heavily contended.</para>
    ///
    /// <para>Modelled rather than raced, for the same reason as the test above: racing a real shell
    /// reproduces this only under load, and a guard that needs luck is not a guard.</para>
    /// </summary>
    [TestMethod]
    public void A_child_that_reaps_late_is_still_reported() => HeadlessUi.RunAsync(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = HeadlessUi.Show(view);
        HeadlessUi.Pump(window);

        var reported = new List<ProcessExitedEventArgs>();
        view.ProcessExited += (_, e) => reported.Add(e);

        // Misses the read loop's grace period, reaps on a later attempt — exactly a loaded box.
        var connection = new ExitedButNotYetReaped(realExitCode: 3, reapsOnCall: 3);
        view.AttachConnection(connection);

        await HeadlessUi.WaitUntil(() => reported.Count > 0,
            "the exit is reported even though the reap missed the read loop's grace period");

        reported.Should().ContainSingle("one exit, reported once");
        reported[0].ExitCodeKnown.Should().BeTrue("the child did reap, so the code is trustworthy");
        reported[0].ExitCode.Should().Be(3,
            "a late reap still yields the REAL code — giving up was what lost it");

        window.Close();
    });
}
