using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using System.Text;

namespace Terminal.CaptureProbe;

/// <summary>
/// A window built to be photographed.
/// </summary>
/// <remarks>
/// <para>Every harness so far rendered synchronously into a bitmap on the UI thread, which bypasses
/// the one thing it cannot fake: the compositor. The real app records Render into a deferred display
/// list that the RENDER THREAD rasterises later, and the remaining glitch lives somewhere my
/// synchronous harnesses provably do not. So this probe does not render anything itself -- it puts a
/// real terminal on a real screen and lets the operating system photograph the compositor's actual
/// output, while dumping the buffer's text alongside for the comparison.</para>
/// <para>Borderless and at a fixed position, so a screenshot region maps to the cell grid by plain
/// arithmetic. The metrics needed for that arithmetic are written next to the dumps.</para>
/// </remarks>
internal static class Program
{
    private const int PosX = 40;
    private const int PosY = 60;

    [STAThread]
    public static int Main(string[] args) =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
}

public class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dir = Environment.GetEnvironmentVariable("PROBE_DIR") ?? "/tmp/probe";
            Directory.CreateDirectory(dir);

            var view = new TerminalView
            {
                Process = Environment.GetEnvironmentVariable("PROBE_CMD") ?? "asciiquarium",
            };

            var window = new Window
            {
                Width = 1100,
                Height = 700,
                CanResize = false,
                Position = new PixelPoint(40, 60),
                Content = view,
                Background = Brushes.Black,
                Topmost = true,
            };

            var seq = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) =>
            {
                // The dump and the metrics travel together, atomically per file: write to a temp
                // name and rename, so the shell side never reads half a dump.
                var t = view.Terminal;
                var sb = new StringBuilder();
                var top = t.Buffer.ViewportY;
                for (var r = 0; r < t.Rows; r++)
                    sb.AppendLine(t.Buffer.GetLine(top + r)?.TranslateToString(false) ?? "");

                var scaling = window.RenderScaling;

                // The exact SCREEN pixel of the view's own (0,0), asked of the platform rather than
                // computed from window position and guessed chrome. This is what lets a screenshot
                // region be mapped to the cell grid by plain arithmetic.
                var origin = view.PointToScreen(new Point(0, 0));
                var meta = $"{view.CharWidth}|{view.CharHeight}|{t.Cols}|{t.Rows}|{scaling}|{origin.X}|{origin.Y}";

                var n = Interlocked.Increment(ref seq);
                var tmp = Path.Combine(dir, "dump.tmp");
                File.WriteAllText(tmp, meta + "\n" + sb);
                File.Move(tmp, Path.Combine(dir, $"dump-{n:D4}.txt"), overwrite: true);
            };

            window.Opened += (_, _) => timer.Start();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
