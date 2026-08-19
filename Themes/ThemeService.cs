using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;

namespace Synclo.Themes;

public interface IThemeService
{
    void ApplyTheme(string theme);
    void ApplyMica(bool enabled, Window? window = null);
}

public sealed class ThemeService : IThemeService
{
    public void ApplyTheme(string theme)
    {
        if (Application.Current == null) return;

        Application.Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    public void ApplyMica(bool enabled, Window? window = null)
    {
        var targetWindow = window;
        if (targetWindow == null && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            targetWindow = desktop.MainWindow;
            if (targetWindow == null)
            {
                foreach (var win in desktop.Windows)
                {
                    if (win.GetType().Name == "MainWindow")
                    {
                        targetWindow = win;
                        break;
                    }
                }
            }
        }

        if (targetWindow == null) return;

        var contentBorder = targetWindow.FindControl<Border>("ContentBorder");
        bool isMicaSupported = OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= 22000;
        if (enabled && isMicaSupported)
        {
            targetWindow.TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica };
            targetWindow.Bind(Window.BackgroundProperty, new DynamicResourceExtension("MicaWindowBackground"));
            if (contentBorder != null)
            {
                contentBorder.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MicaContentBackground"));
            }
        }
        else
        {
            targetWindow.TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            targetWindow.Bind(Window.BackgroundProperty, new DynamicResourceExtension("PrimaryBackground"));
            if (contentBorder != null)
            {
                contentBorder.Bind(Border.BackgroundProperty, new DynamicResourceExtension("SecondaryBackground"));
            }
        }
    }
}
