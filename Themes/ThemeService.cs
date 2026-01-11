using Avalonia;
using Avalonia.Styling;

namespace Synclo.Themes;

public interface IThemeService
{
    void ApplyTheme(string theme);
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
}
