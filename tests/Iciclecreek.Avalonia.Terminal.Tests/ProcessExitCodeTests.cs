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

    // ── The relaunch race ───────────────────────────────────────────────────────────────────────


    /// <summary>
    /// <see cref="TerminalView.DetachConnection"/> gives the implicit detach a name: it hands the
    /// connection back, leaves the process running, and leaves the view with nothing attached.
    ///
    /// <para>The assertions are the same three the attached-connection guard makes, which is the point —
    /// the named operation and the side effect of cleanup must agree, or having both is worse than
    /// having one.</para>
    /// </summary>
    [TestMethod]
    public void DetachConnection_hands_the_connection_back_alive() => HeadlessUi.RunAsync(async () =>
    {
        var view = new TerminalView();
        var window = HeadlessUi.Show(view);
        HeadlessUi.Pump(window);

        var attached = new ParkedUntilReleased(realExitCode: 0);
        view.AttachConnection(attached);
        view.IsLive.Should().BeTrue("the view was just handed a live connection");

        var returned = view.DetachConnection();

        returned.Should().BeSameAs(attached, "the caller gets back exactly what it handed over");
        attached.Disposed.Should().BeFalse(
            "detaching must not dispose — disposing a pty ends the child, which is the opposite of detaching");
        view.IsLive.Should().BeFalse("the view is following nothing now");
        view.DetachConnection().Should().BeNull("nothing left to detach");

        attached.Release();
        await Task.Yield();
        window.Close();
    });

    /// <summary>
    /// A connection whose reader BLOCKS until it is released, then returns EOF — which is what a real one does
    /// when a relaunch disposes it out from under a parked read. On Unix the reader wraps a synchronous
    /// FileStream, so cancellation does not reliably interrupt it: the read returns, and whichever loop was
    /// sitting in it wakes up holding a connection that may no longer be the live one.
    /// </summary>

    /// <summary>
    /// An attached connection must survive the view: not killed, and NOT DISPOSED.
    ///
    /// <para>Disposing is not a neutral detach — it ends the child. Measured on both platforms, with no
    /// <c>Kill()</c> anywhere: the process is gone within 300ms on Windows (<c>PseudoConsoleConnection</c>)
    /// and on Unix, where closing the master fd sends <c>SIGHUP</c> to the foreground process group. So a
    /// host that closes a pane or re-parents a view would lose the process it owns — the exact thing
    /// <see cref="TerminalView.AttachConnection"/> exists to make safe.</para>
    ///
    /// <para>This assertion is why the fake records disposal at all. A fake whose <c>Dispose</c> is a no-op
    /// satisfies the contract no matter what the view does, which is how the earlier revision of this branch
    /// passed its tests while disposing every attached connection.</para>
    /// </summary>
    [TestMethod]
    public void An_attached_connection_is_neither_killed_nor_disposed() => HeadlessUi.RunAsync(async () =>
    {
        var view = new TerminalView();
        var window = HeadlessUi.Show(view);
        var attached = new ParkedUntilReleased(realExitCode: 0);

        view.AttachConnection(attached);
        view.IsLive.Should().BeTrue("the view was just handed a live connection");

        // Replacing it is the detach path a pane close or re-parent takes.
        view.AttachConnection(new ParkedUntilReleased(realExitCode: 0));
        await Task.Yield();

        attached.Disposed.Should().BeFalse(
            "the view disposed a connection it does not own; disposing ends the child, so a host would lose "
            + "the process behind a pane it merely closed");

        attached.Release();
    });

    private sealed class ParkedUntilReleased : IPtyConnection
    {
        private readonly ManualResetEventSlim _release = new(false);

        public ParkedUntilReleased(int realExitCode) => ExitCode = realExitCode;

        /// <summary>Let the parked read return EOF, as a disposed stream would.</summary>
        public void Release() => _release.Set();

        public int ExitCode { get; }

        public bool WaitForExit(int milliseconds) => true;   // already dead by the time anyone asks

        public Stream ReaderStream => field ??= new BlockingEofStream(_release);
        public Stream WriterStream { get; } = new MemoryStream();

        public int Pid => -1;
        public void Kill() { }
        public void Resize(int columns, int rows) { }
        /// <summary>Whether the view disposed this connection. It must not, for an attached one.</summary>
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            _release.Set();
        }

        public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }

        private sealed class BlockingEofStream(ManualResetEventSlim release) : Stream
        {
            public override int Read(byte[] buffer, int offset, int count)
            {
                release.Wait();
                return 0;   // EOF, exactly as a closed pty master reports
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => 0;
            public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => 0;
            public override void SetLength(long value) { }
            public override void Write(byte[] buffer, int offset, int count) { }
        }
    }

    /// <summary>
    /// A read loop whose connection was replaced while it was parked must NOT report an exit — not for itself,
    /// and above all not against its successor.
    ///
    /// <para>The window is narrow but entirely reachable, and it is the one Copilot flagged on the upstream PR.
    /// The loop's ownership test is its <c>while</c> condition, evaluated BEFORE the blocking read. Attaching a
    /// new connection swaps <c>_ptyConnection</c> and arms a fresh interlock; when the old stream then reports
    /// EOF the stale loop walks into the exit path, and with a bare <c>Interlocked.Exchange</c> its claim
    /// SUCCEEDS — because the flag it finds was reset for the new process. The visible result is a
    /// freshly-started terminal that immediately prints the previous process's exit and reports itself dead.</para>
    /// </summary>
    [TestMethod]
    public void A_Stale_Read_Loop_Cannot_Report_An_Exit_Against_Its_Successor() => HeadlessUi.RunAsync(async () =>
    {
        var view = new TerminalView { Process = "" };
        var window = HeadlessUi.Show(view);

        var reported = new List<int?>();
        view.ProcessExited += (_, e) => reported.Add(e.ExitCodeKnown ? e.ExitCode : null);

        // First connection: its reader is parked, so its loop is sitting in the read.
        var first = new ParkedUntilReleased(realExitCode: 3);
        view.AttachConnection(first);
        await Task.Delay(150);

        // The relaunch. This arms a fresh interlock for `second`.
        var second = new ParkedUntilReleased(realExitCode: 0);
        view.AttachConnection(second);
        await Task.Delay(50);

        // Now let the FIRST connection's parked read return EOF. Its loop wakes holding a connection the view
        // no longer owns. This line is what frees it: an attached connection is not disposed on replacement,
        // so nothing else has set the gate. (It was not always load-bearing — while the view still disposed
        // attached connections, the replacement above freed the read and this call only looked like it did.)
        first.Release();
        await Task.Delay(400);

        reported.Should().BeEmpty(
            "the stale loop's connection is not the live one, so it has no exit to report — and reporting one "
            + "would both print the wrong process's code and mark the NEW connection as already exited");
        view.IsLive.Should().BeTrue(
            "the terminal was just handed a live connection; a stale loop must not be able to kill it");

        second.Release();
        HeadlessUi.Pump(window);
    });
}
