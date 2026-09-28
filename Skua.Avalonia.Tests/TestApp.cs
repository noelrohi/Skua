using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Skua.Avalonia.Tests.TestApp))]
// One app and dispatcher for the whole run, as the Mac App has one UI thread for its life: the tests share one Engine and Core's view models,
// whose async work started in one test (a Script list refresh, say) would otherwise be lost with that test's dispatcher, and hang.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace Skua.Avalonia.Tests;

/// <summary>The headless app the tests run in: Skia drawing, so a test can read the pixels the Game View drew.</summary>
public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
