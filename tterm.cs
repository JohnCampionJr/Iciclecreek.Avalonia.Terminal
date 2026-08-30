#!/usr/bin/env dotnet
// A terminal in one file. From any checkout root:
//
//   dotnet run tterm.cs                 -- your shell
//   dotnet run tterm.cs -- asciiquarium -- any command
//
// The #:project line points at the library RELATIVE TO THIS FILE, so copying this file into a
// bisect worktree makes it run that worktree's code -- launch, look, verdict, no project to rig up.
#:sdk Microsoft.NET.Sdk
#:property OutputType=WinExe
#:package Avalonia.Desktop@12.0.2
#:package Avalonia.Themes.Fluent@12.0.2
#:project src/Iciclecreek.Avalonia.TerminalWindow/Iciclecreek.Avalonia.Terminal.csproj

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Iciclecreek.Terminal;

AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);

class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
        {
            var cmd = d.Args is { Length: > 0 } a ? a[0]
                : Environment.GetEnvironmentVariable("SHELL") ?? "bash";

            d.MainWindow = new Window
            {
                Title = $"tterm — {cmd}",
                Width = 1100,
                Height = 700,
                Background = Brushes.Black,
                Content = new TerminalView
                {
                    Process = cmd,
                    Args = d.Args is { Length: > 1 } r ? r[1..] : [],
                },
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
