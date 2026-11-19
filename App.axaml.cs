using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using SqlStudio.Models;
using SqlStudio.Services;
using SqlStudio.ViewModels;
using SqlStudio.Views;

namespace SqlStudio;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Colours must exist before the first window is created, because every brush is looked up by name.
        ThemeService.Apply(AppThemes.ById(new SettingsStore().Load().ThemeId));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
            desktop.MainWindow = window;

            // Clicking the Dock icon while the window is hidden (red button) brings it back.
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.Reopen)
                        Dispatcher.UIThread.Post(window.BringToFront);
                };
        }

        base.OnFrameworkInitializationCompleted();

        // After the window is up: AppKit owns the Dock icon and may reset it while the first window opens.
        Dispatcher.UIThread.Post(SetDockIcon, DispatcherPriority.Background);
    }

    static void SetDockIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://SqlStudio/Assets/AppIcon.png"));
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            MacDockIcon.TrySet(memory.ToArray());
        }
        catch (Exception)
        {
            // a missing Dock icon must never stop the app from starting
        }
    }
}