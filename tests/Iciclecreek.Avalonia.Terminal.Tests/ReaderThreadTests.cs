using System.Diagnostics;
using Porta.Pty;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// The PTY read loop must run on a thread the view OWNS, not on the thread pool.
///
/// <para>This is the guard for a defect that is invisible in ordinary use and severe under load, and
/// it is worth stating precisely because the code that had it LOOKED correct. The reader is started
/// with <c>TaskCreationOptions.LongRunning</c>, which reads as "give this its own thread" — but
/// LongRunning owns that thread only up to the first <c>await</c> that YIELDS. Every continuation
/// after that is scheduled on <c>TaskScheduler.Default</c>, i.e. the pool. So a loop written as
/// <c>await ReaderStream.ReadAsync(...)</c> is on a dedicated thread for exactly one read and on the
/// pool forever after.</para>
///
/// <para>On Windows that is worse than it sounds. The stream underneath is a <c>FileStream</c> opened
/// <c>isAsync: false</c>, whose <c>ReadAsync</c> performs no overlapped I/O — it parks a POOL thread in
/// a blocking read. ConPTY does not signal EOF while the pseudoconsole is open, so that thread stays
/// parked for the whole life of the process rather than until the child writes. Several terminals at
/// once therefore park several pool threads, on a pool that grows at roughly one thread per second.</para>
///
/// <para>Measured downstream in Tweed's equivalent loop, 24 concurrent short-lived processes on a
/// 4-vCPU box: 137 ms to first output with a dedicated thread, 7546 ms pooled — and under real load
/// the pooled form lost the output ENTIRELY rather than merely delaying it, while still reporting a
/// clean exit.</para>
///
/// <para>Guarding the thread rather than the timing is deliberate. A latency assertion would be
/// load-sensitive and would fail on a busy runner for reasons that are not this bug; "which thread am
/// I on" is exact, cheap, and cannot pass against the regression.</para>
/// </summary>
[TestClass]
public class ReaderThreadTests
{
    [TestMethod]
    public void The_read_loop_does_not_run_on_the_thread_pool() => HeadlessUi.RunAsync(async () =>
    {
        var connection = new ThreadRecordingConnection();

        var view = new TerminalView { Process = "" };
        var window = HeadlessUi.Show(view);
        HeadlessUi.Pump(window);

        view.AttachConnection(connection);

        var read = await Task.WhenAny(connection.SecondRead.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.AreSame(connection.SecondRead.Task, read, "the reader never got as far as a second read");

        Assert.IsFalse(
            connection.SecondReadWasOnThePool,
            "the PTY read loop ran on a thread-pool thread. LongRunning does not survive an await that "
            + "yields, so the loop must read SYNCHRONOUSLY on the thread it was handed.");
    });

    /// <summary>
    /// Records which kind of thread issued the SECOND read, and makes the first one genuinely yield.
    ///
    /// <para>Both details were found by mutation-testing this guard, and without either of them it
    /// passes against the very code it exists to reject.</para>
    ///
    /// <para>The FIRST read is on the dedicated thread either way — <c>LongRunning</c> does hand the
    /// delegate its own thread, and the loop reaches its first read before any <c>await</c> can yield.
    /// The divergence starts at the second. So the second is what is recorded.</para>
    ///
    /// <para>And the first read has to return an INCOMPLETE task. <c>await</c> on an
    /// already-completed task does not yield at all; it continues inline on the same thread. A fake
    /// returning <c>Task.FromResult</c> therefore keeps the pooled loop on the dedicated thread
    /// forever and reports no problem. Real streams block, so yielding here is the faithful model as
    /// well as the discriminating one.</para>
    /// </summary>
    private sealed class ThreadRecordingConnection : IPtyConnection
    {
        public ThreadRecordingConnection() => ReaderStream = new RecordingStream(this);

        public TaskCompletionSource SecondRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SecondReadWasOnThePool { get; private set; }

        public Stream ReaderStream { get; }

        public Stream WriterStream { get; } = new MemoryStream();

        public int Pid => -1;

        public int ExitCode => 0;

        public bool WaitForExit(int milliseconds) => true;

        public void Kill()
        {
        }

        public void Resize(int columns, int rows)
        {
        }

        public void Dispose()
        {
        }

        public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }

        private void NoteSecondRead()
        {
            if (SecondRead.Task.IsCompleted)
            {
                return;
            }

            SecondReadWasOnThePool = Thread.CurrentThread.IsThreadPoolThread;
            SecondRead.TrySetResult();
        }

        private sealed class RecordingStream : Stream
        {
            private static readonly byte[] Payload = "hello"u8.ToArray();

            private readonly ThreadRecordingConnection _owner;
            private int _reads;

            public RecordingStream(ThreadRecordingConnection owner) => _owner = owner;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => 0;

            public override long Position { get => 0; set { } }

            /// <summary>What the fixed loop calls. Always on the thread StartNew handed it.</summary>
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Interlocked.Increment(ref _reads) == 1)
                {
                    Payload.CopyTo(buffer.AsSpan(offset));
                    return Payload.Length;
                }

                _owner.NoteSecondRead();
                return 0;
            }

            /// <summary>
            /// What a regressed loop calls. The first one YIELDS, which is what moves everything after
            /// it onto the pool — and is also how a real stream behaves.
            /// </summary>
            public override async Task<int> ReadAsync(
                byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref _reads) == 1)
                {
                    await Task.Yield();
                    Payload.CopyTo(buffer.AsSpan(offset));
                    return Payload.Length;
                }

                _owner.NoteSecondRead();
                return 0;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => 0;

            public override void SetLength(long value)
            {
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
            }
        }
    }
}
