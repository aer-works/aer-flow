using Aer.Ui;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace Aer.Ui.Tests;

/// <summary>
/// Guards #456's font switch against the one way it fails silently. An <c>avares://…#Family Name</c>
/// reference is resolved by string: if the name after the <c>#</c> does not match the family name
/// recorded inside the shipped <c>.ttf</c>, Avalonia does not throw, does not warn, and does not
/// render a missing-font box — it quietly falls back to the platform default. The app still looks
/// like an app; it just is not wearing the face the owner chose, and it wears a different one on
/// every OS, which is exactly what decision 0006 exists to prevent.
/// </summary>
/// <remarks>
/// This is a live resolution test rather than a check that the files exist, because file presence
/// is not the failing half — a correct file with a family name one character off fails identically
/// to a missing one. These assertions go through <see cref="FontManager"/>, the same path the
/// renderer uses.
/// </remarks>
public class ShippedTypefaceTests
{
    [AvaloniaFact]
    public void The_prose_face_resolves_to_the_shipped_source_sans_and_not_a_platform_fallback()
    {
        Assert.True(
            FontManager.Current.TryGetGlyphTypeface(new Typeface(new FontFamily(AerFonts.Sans)), out var glyphTypeface),
            $"{AerFonts.Sans} did not resolve to any typeface at all.");

        // The teeth: a fallback resolves successfully too, so success alone proves nothing. Only the
        // family name distinguishes "found the shipped asset" from "quietly used Segoe UI".
        Assert.Equal("Source Sans 3", glyphTypeface.FamilyName);
    }

    [AvaloniaFact]
    public void The_code_face_resolves_to_the_shipped_jetbrains_mono_and_not_a_platform_fallback()
    {
        Assert.True(
            FontManager.Current.TryGetGlyphTypeface(new Typeface(new FontFamily(AerFonts.Mono)), out var glyphTypeface),
            $"{AerFonts.Mono} did not resolve to any typeface at all.");

        Assert.Equal("JetBrains Mono", glyphTypeface.FamilyName);
    }

    /// <summary>
    /// Source Sans 3 ships as a variable font whose <c>wght</c> axis defaults to 200 (ExtraLight) —
    /// its unmodified release renders every unweighted run of text noticeably thin. It was
    /// re-defaulted to 400 before being committed (#453). Nothing else catches a regression there:
    /// the app builds, the font resolves, and the only symptom is that the UI looks wispy.
    /// </summary>
    [AvaloniaFact]
    public void The_prose_face_defaults_to_regular_weight_not_the_variable_fonts_extralight_default()
    {
        Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(new FontFamily(AerFonts.Sans)), out var glyphTypeface));

        Assert.Equal(FontWeight.Normal, glyphTypeface.Weight);
    }

    /// <summary>
    /// The app-wide default, which is what every control that never names a family renders in. Set
    /// on the <see cref="Avalonia.AppBuilder"/> rather than as a style, so a control outside the
    /// window's resource scope (a flyout, a tooltip) gets it too.
    /// </summary>
    [AvaloniaFact]
    public void Text_that_names_no_family_falls_back_to_the_shipped_prose_face()
    {
        var defaultFamily = FontManager.Current.DefaultFontFamily;

        Assert.Equal("Source Sans 3", defaultFamily.Name);

        // Name alone would also pass for a same-named font installed on the build machine. Key is
        // the asset URI the family was constructed from, and is null for a bare system-font name —
        // so this is the half that proves the default is the copy shipped in this repo.
        AssertResolvesToAShippedAsset(defaultFamily);
    }

    /// <summary>
    /// The <c>.mono</c> class is how every code/transcript surface in the app opts into the code
    /// face. It previously named a <c>Cascadia Mono,Consolas,Menlo,monospace</c> chain, which
    /// resolved to a different face per OS; #456 repointed it at the generated <c>FontMono</c>.
    /// </summary>
    [AvaloniaFact]
    public void The_mono_style_class_resolves_to_the_shipped_code_face()
    {
        var window = new Window { Content = new TextBlock { Classes = { "mono" } } };
        window.Show();

        var textBlock = (TextBlock)window.Content!;

        Assert.Equal("JetBrains Mono", textBlock.FontFamily.Name);
        AssertResolvesToAShippedAsset(textBlock.FontFamily);
    }

    /// <summary>
    /// A <see cref="FontFamily"/> built from a bare name has a null <see cref="FontFamily.Key"/>;
    /// one built from an <c>avares://</c> reference carries the asset URI there. Checking it is what
    /// separates "this repo's font" from "a font that happens to be installed on this machine" —
    /// and the latter passing on a developer's box while failing in CI is the whole failure mode.
    /// </summary>
    private static void AssertResolvesToAShippedAsset(FontFamily family)
    {
        Assert.NotNull(family.Key);
        Assert.Contains("Aer.Ui/Assets/Fonts", family.Key!.Source.ToString());
    }
}
