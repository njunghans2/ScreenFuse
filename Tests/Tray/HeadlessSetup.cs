using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Tests.Tray.HeadlessSetup))]

namespace Tests.Tray;

// Lets the settings window be built and driven in a test. The interface was the one part of
// ScreenFuse with no tests at all, and it is where a scroll of the page could switch a monitor.
public class HeadlessSetup : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<HeadlessSetup>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
