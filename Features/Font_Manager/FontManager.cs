using System;
using Avalonia;
using Avalonia.Media;

namespace Synclo.Features.Font_Manager;

public interface IFontManager
{
    void ApplyFont(AppFonts font);
    void ApplyFont(string fontName);
    FontFamily GetFontFamily(AppFonts font);
    FontFamily GetFontFamily(string fontName);
}

public sealed class FontManager : IFontManager
{
    private const string InconsolataUri = "avares://Synclo/Assets/Fonts/Inconsolata#Inconsolata";

    public FontFamily GetFontFamily(AppFonts font)
    {
        return font switch
        {
            AppFonts.System => FontFamily.Default,
            AppFonts.Inconsolata => new FontFamily(InconsolataUri),
            _ => new FontFamily(InconsolataUri)
        };
    }

    public FontFamily GetFontFamily(string fontName)
    {
        if (Enum.TryParse<AppFonts>(fontName, true, out var parsedFont))
        {
            return GetFontFamily(parsedFont);
        }

        return GetFontFamily(AppFonts.Inconsolata);
    }

    public void ApplyFont(AppFonts font)
    {
        if (Application.Current == null) return;

        var fontFamily = GetFontFamily(font);
        Application.Current.Resources["AppFontFamily"] = fontFamily;
    }

    public void ApplyFont(string fontName)
    {
        if (Enum.TryParse<AppFonts>(fontName, true, out var parsedFont))
        {
            ApplyFont(parsedFont);
        }
        else
        {
            ApplyFont(AppFonts.Inconsolata);
        }
    }
}
