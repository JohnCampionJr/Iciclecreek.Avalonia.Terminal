using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

// The application this assembly's headless session builds. Avalonia's own attribute, so the declaration
// is the same one Avalonia.Headless.NUnit and .XUnit consumers already write. Point it at your own
// Application subclass and this file needs no edit at all.
[assembly: AvaloniaTestApplication(typeof(HeadlessTestApp))]

// DELIBERATELY no namespace. This file is meant to be copied verbatim into any MSTest project that
// needs Avalonia, and a namespace is the one line every copy would otherwise have to edit — which is
// how copies drift. Global-namespace types are visible from every test class without a using, so a
// paste needs no follow-up at all. Point the attribute above at your own Application if you have one.

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
    internal static readonly Lazy<HeadlessUnitTestSession> Session = new(() =>
        HeadlessUnitTestSession.StartNew(
            // Discovered from [assembly: AvaloniaTestApplication(...)] rather than named here, so this
            // file carries nothing specific to one repo. GetOrStartForAssembly would do the discovery
            // too and is NOT usable: it takes no isolation argument and hands out a fresh Application
            // per test — measured — which is the mode this whole comment is about avoiding.
            typeof(HeadlessUi).Assembly
                .GetCustomAttribute<AvaloniaTestApplicationAttribute>()?.AppBuilderEntryPointType
                ?? throw new InvalidOperationException(
                    "No [assembly: AvaloniaTestApplication(typeof(YourApp))] found. Declare one; without "
                    + "it there is no Application to build and every Avalonia type fails to construct."),
            AvaloniaTestIsolationLevel.PerAssembly));

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
    public static Window Show(Control content, double width = 520, double height = 900)
    {
        var window = new Window { Width = width, Height = height, Content = content };
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

/// <summary>
/// <c>[AvaloniaTest]</c> — <c>[TestMethod]</c> that runs its body through <see cref="HeadlessUi"/>.
///
/// <para>Behaviourally identical to writing <c>HeadlessUi.RunAsync(async () =&gt; { ... })</c> by hand:
/// the same PerAssembly session, and the same <c>finally</c> that closes windows the body left open. It
/// exists only so a test body is a test body rather than a lambda inside one — which also removes the
/// chance of someone reaching for the wrong <c>Dispatch</c> overload when adding the next helper.</para>
///
/// <para>Copy this file, or just this class, into any MSTest project that needs Avalonia. It is
/// deliberately not a package: what is hard here is the two lines of session setup above and the
/// reasons written around them, and those are worth reading where the tests are.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class AvaloniaTestAttribute : TestMethodAttribute
{
    /// <param name="callerFilePath">Compiler-supplied; do not pass.</param>
    /// <param name="callerLineNumber">Compiler-supplied; do not pass.</param>
    /// <remarks>
    /// Forwarded so a test reports ITS declaring file and line rather than this attribute's — what a test
    /// explorer navigates to, and what a failure is attributed to. Defaults mirror the base exactly
    /// ("" and -1): the base declares callerFilePath non-nullable, so a nullable parameter here is a
    /// CS8604 at the base call. MSTEST0057 is the analyzer that catches omitting them.
    /// </remarks>
    public AvaloniaTestAttribute(
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = -1)
        : base(callerFilePath, callerLineNumber)
    {
    }

    /// <inheritdoc/>
    public override Task<TestResult[]> ExecuteAsync(ITestMethod testMethod) =>
        // The Func<Task<T>> overload, NOT Func<T>. They differ by return type alone, and choosing the
        // wrong one does not fail cleanly — it deadlocks the run.
        HeadlessUi.Session.Value.Dispatch<TestResult[]>(
            async () =>
            {
                try { return await base.ExecuteAsync(testMethod); }
                finally { HeadlessUi.CloseOpenWindows(); }
            },
            CancellationToken.None);
}
