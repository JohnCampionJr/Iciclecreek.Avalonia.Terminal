using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NUnit.Framework;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// A window query in the output must not freeze the UI thread.
/// </summary>
/// <remarks>
/// <para>The reader writes each chunk inside <c>_terminalLock</c>, and the UI thread takes that lock in
/// ClearScreen, CurrentLineText, WriteOwnLine and ArrangeOverride. Window queries used to be answered by
/// waiting on the UI thread, up to a 250 ms patience per query -- and a UI thread blocked on the lock cannot
/// run the job being waited for, so every query in the chunk sat its patience out. 20 queries blocked
/// ClearScreen for ~4.9 s; <c>yes $'\e[13t'</c> for minutes.</para>
/// <para>Measured from the UI thread's side, through the real read loop, which is the only place the
/// freeze shows.</para>
/// </remarks>
[TestFixture]
public class WindowQueryUnderLockReproTests
{
    private static readonly string Esc = ((char)0x1B).ToString();

    private static TimeSpan ClearScreenBlockedFor(int queries, string query, bool enablePosition)
    {
        var view = new TerminalView { Process = "" };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        try
        {
            if (enablePosition)
                view.Terminal.Options.WindowOptions.GetWinPosition = true;
            var pty = new PushConnection();
            view.AttachConnection(pty);
            pty.Push(string.Concat(Enumerable.Repeat(query, queries)));
            Thread.Sleep(100);
            var clock = Stopwatch.StartNew();
            view.ClearScreen();
            TestContext.Out.WriteLine($"{queries} x {query.Substring(1)}: UI thread blocked {clock.Elapsed.TotalMilliseconds:F0} ms");
            return clock.Elapsed;
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void One_query_costs_at_most_one_patience() =>
        Assert.That(ClearScreenBlockedFor(1, Esc + "[13t", true), Is.LessThan(TimeSpan.FromMilliseconds(400)));

    [AvaloniaTest]
    public void Twenty_queries_do_not_block_the_UI_thread() =>
        Assert.That(ClearScreenBlockedFor(20, Esc + "[13t", true), Is.LessThan(TimeSpan.FromMilliseconds(50)));
}
