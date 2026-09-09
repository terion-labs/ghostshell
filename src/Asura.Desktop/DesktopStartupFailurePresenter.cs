using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Asura.Desktop;

internal static class DesktopStartupFailurePresenter
{
    public static void TryShow(
        string title,
        string message,
        string[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(arguments);

        try
        {
            AppBuilder
                .Configure(() => new StartupFailureApplication(title, message))
                .UsePlatformDetect()
                .WithInterFont()
                .StartWithClassicDesktopLifetime(
                    arguments,
                    ShutdownMode.OnMainWindowClose);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // stderr remains the deterministic fallback for headless or unavailable desktops.
            Asura.Application.SecretSafeDiagnosticProjection.WriteStandardError(
                "desktop.startup-message.failed",
                exception);
        }
    }

    private sealed class StartupFailureApplication(
        string title,
        string message) : Avalonia.Application
    {
        public override void Initialize()
        {
            RequestedThemeVariant = ThemeVariant.Dark;
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow = CreateWindow(title, message);
            }

            base.OnFrameworkInitializationCompleted();
        }

        private static Window CreateWindow(string title, string message)
        {
            var closeButton = new Button
            {
                Content = "Close",
                HorizontalAlignment = HorizontalAlignment.Right,
                MinWidth = 96,
            };
            AutomationProperties.SetName(closeButton, "Close startup message");

            var window = new Window
            {
                Title = "Asura",
                Width = 480,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new Border
                {
                    Padding = new Thickness(24),
                    Child = new StackPanel
                    {
                        Spacing = 14,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = title,
                                FontSize = 20,
                                FontWeight = FontWeight.SemiBold,
                                TextWrapping = TextWrapping.Wrap,
                            },
                            new TextBlock
                            {
                                Text = message,
                                FontSize = 14,
                                Opacity = 0.82,
                                TextWrapping = TextWrapping.Wrap,
                            },
                            closeButton,
                        },
                    },
                },
            };
            AutomationProperties.SetName(window, title);
            closeButton.Click += (_, _) => window.Close();
            return window;
        }
    }
}
