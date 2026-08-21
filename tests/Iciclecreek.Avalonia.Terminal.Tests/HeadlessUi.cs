using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Minimal <see cref="Application"/> for headless view tests. Loads <see cref="FluentTheme"/> because
/// control TEMPLATES are needed for a templated control to realise anything at all.
/// </summary>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

/// <summary>
/// Shared headless session plus the few helpers these tests need. Avalonia can only be initialised once
/// per process, so there is one session per test assembly and each body is dispatched onto its UI thread.
/// </summary>
public static class HeadlessUi
{
    // PerAssembly: ONE Application + Dispatcher for the whole assembly. The default isolation rebuilds
    // the application on EVERY dispatch, and that construction path — the compositor hooking the render
    // loop — intermittently dies with a VerifyAccess ("a different thread owns it") before any test body
    // runs. Building once removes the repeated trips through that racy path. The trade, per Avalonia's
    // own remarks, is that state leaks between tests: a window a test leaves open stays open, which is
    // why Run closes them.
    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp), AvaloniaTestIsolationLevel.PerAssembly));

    /// <summary>Run a body on the headless UI thread, closing any window it opened afterwards.</summary>
    public static void Run(Action body) => Session.Value.Dispatch(() =>
    {
        try { body(); }
        finally { CloseOpenWindows(); }
    }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>As <see cref="Run(Action)"/>, for a body that awaits.</summary>
    /// <remarks>
    /// The lambda returns a value deliberately. Passing a bare <c>Func&lt;Task&gt;</c> binds to the
    /// <c>Dispatch&lt;T&gt;(Func&lt;T&gt;)</c> overload with <c>T = Task</c>, which treats the returned
    /// task as a plain RESULT and never awaits it — the body runs to its first <c>await</c> and the test
    /// passes having asserted nothing.
    /// </remarks>
    public static void RunAsync(Func<Task> body) => Session.Value.Dispatch(async () =>
    {
        try { await body(); }
        finally { CloseOpenWindows(); }
        return true;
    }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Spin until <paramref name="condition"/> holds, or fail. Polls a real signal rather
    /// than sleeping a guessed interval — the paths under test hop threads and a dispatcher, so
    /// "it will have happened by now" is exactly the assumption that fails on a busy machine. The
    /// ceiling is generous because it is a bound, not a measurement of the host.</summary>
    public static async Task WaitUntil(Func<bool> condition, string because, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"timed out after {timeoutMs}ms waiting until {because}");
            await Task.Delay(10);
        }
    }

    /// <summary>Host a control in a real window and lay it out, so the visual tree exists and the input
    /// path (hit test → routed event) is the real one.</summary>
    public static Window Show(Control content)
    {
        var window = new Window { Width = 520, Height = 900, Content = content };
        window.Show();
        _openWindows.Add(window);
        window.Closed += (_, _) => _openWindows.Remove(window);
        Pump(window);
        return window;
    }

    private static readonly List<Window> _openWindows = [];

    public static void CloseOpenWindows()
    {
        foreach (var window in _openWindows.ToList())
            window.Close();
    }

    /// <summary>Force a layout + render pass so freshly-raised property changes are reflected.</summary>
    public static void Pump(Window window)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
}
