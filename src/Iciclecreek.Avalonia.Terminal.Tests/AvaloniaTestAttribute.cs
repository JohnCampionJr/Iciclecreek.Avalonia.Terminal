// Vendored from jcamp.Avalonia.Headless.MsTest (John's MSTest sibling of Avalonia.Headless.NUnit),
// the copy-and-paste way that project is meant to be consumed.
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;

namespace Iciclecreek.Terminal.Tests;

/// <summary>
/// Runs an MSTest method on Avalonia's headless UI thread.
/// </summary>
/// <remarks>
/// <para>Use it exactly where you would use <c>[TestMethod]</c>, and declare the application once for the
/// assembly:</para>
/// <code>
/// [assembly: AvaloniaTestApplication(typeof(MyTestApp))]
///
/// [TestClass]
/// public class MyTests
/// {
///     [AvaloniaTest]
///     public void A_control_can_be_built() =&gt; new TextBlock();
/// }
/// </code>
/// <para>Avalonia ships this integration for NUnit and xUnit but not for MSTest. Without it a test body
/// runs on whatever thread the runner supplies, and most Avalonia types cannot be constructed there at
/// all — the failure is a <see cref="InvalidOperationException"/> naming a platform service such as
/// <c>ICursorFactory</c>, which reads as a missing dependency rather than as a threading problem.</para>
/// <para>One session per test assembly, because Avalonia can only be initialised once per process. The
/// application type is discovered from the assembly's <see cref="AvaloniaTestApplicationAttribute"/>,
/// the same declaration Avalonia's own NUnit and xUnit integrations expect.</para>
///
/// <para><b>The session is started with an explicit
/// <see cref="AvaloniaTestIsolationLevel.PerAssembly"/>, and that is the most important line in this
/// file.</b> Under the alternative the <see cref="Application"/> and its dispatcher are rebuilt for
/// every test, and that construction path — the compositor hooking the render loop — intermittently
/// dies with a <c>VerifyAccess</c> "a different thread owns it" BEFORE any test body runs. It presents
/// as an unrelated test failing about one run in twenty on a loaded machine: the shape everyone files
/// as flakiness and re-runs. It is not flaky.</para>
///
/// <para><c>HeadlessUnitTestSession.GetOrStartForAssembly</c> is the shorter route and takes no
/// isolation argument, so it reads as though it inherits the enum's default — which IS
/// <c>PerAssembly</c>, value zero. It does not: measured, it hands out a fresh Application per test.
/// A test asserting on the enum passed while this package was doing exactly the wrong thing, which is
/// why the guard asserts on the Application INSTANCE instead.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class AvaloniaTestAttribute : TestMethodAttribute
{
    /// <summary>Creates the attribute, capturing where it was written.</summary>
    /// <param name="callerFilePath">Supplied by the compiler; do not pass it.</param>
    /// <param name="callerLineNumber">Supplied by the compiler; do not pass it.</param>
    /// <remarks>Defaults mirror the base exactly ("" and -1, not null): the base declares
    /// <c>callerFilePath</c> non-nullable, so a nullable parameter here is a CS8604 at the base call.</remarks>
    /// <remarks>
    /// These exist only to be forwarded. <c>TestMethodAttribute</c> takes them so the framework can
    /// report a test's declaring file and line — which is what a test explorer navigates to and what a
    /// failure is attributed to. A derived attribute that does not forward them silently loses that for
    /// every test using it; the compiler fills them in at each call site, so an attribute declared
    /// without them hands the base its own location instead of the test's. MSTEST0057 is the analyzer
    /// that says so.
    /// </remarks>
    public AvaloniaTestAttribute(
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = -1)
        : base(callerFilePath, callerLineNumber)
    {
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Assembly, HeadlessUnitTestSession> Sessions = new();

    /// <inheritdoc/>
    public override Task<TestResult[]> ExecuteAsync(ITestMethod testMethod)
    {
        ArgumentNullException.ThrowIfNull(testMethod);

        var assembly = testMethod.MethodInfo?.DeclaringType?.Assembly
                       ?? throw new InvalidOperationException(
                           "Could not determine the declaring assembly for the test method, so the "
                           + "headless session cannot be resolved.");

        var session = Sessions.GetOrAdd(assembly, a =>
        {
            var entry = a.GetCustomAttribute<AvaloniaTestApplicationAttribute>()?.AppBuilderEntryPointType
                        ?? typeof(global::Avalonia.Application);
            return HeadlessUnitTestSession.StartNew(entry, AvaloniaTestIsolationLevel.PerAssembly);
        });

        // The Func<Task<T>> overload, NOT Func<T>. The two differ by return type alone, so the wrong one
        // is chosen silently — and it does not fail cleanly: picking Func<T> with T = Task<TestResult[]>
        // and unwrapping the task afterwards DEADLOCKS the run rather than passing a vacuous test.
        // Measured: the suite hangs indefinitely instead of reporting anything, which under a runner that
        // absorbs hangs would look like a pass.
        return session.Dispatch<TestResult[]>(
            () => base.ExecuteAsync(testMethod),
            CancellationToken.None);
    }
}
