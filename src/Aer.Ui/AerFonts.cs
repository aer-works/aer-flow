using Avalonia;
using Avalonia.Media;

namespace Aer.Ui;

/// <summary>
/// The app's typeface configuration (#456), applied to the <see cref="AppBuilder"/> rather than to
/// a theme resource because <see cref="FontManagerOptions.DefaultFamilyName"/> is what every
/// control that never sets a <c>FontFamily</c> resolves to — including popups, flyouts and tooltips
/// that live outside the main window's own resource scope.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately shared by <c>Program</c> and the headless test harness rather than duplicated.
/// The test project builds its own <see cref="AppBuilder"/> (it has to — it swaps in the headless
/// platform), so before this existed the tests rendered in a different default face from the
/// shipped app, which makes every font-sensitive assertion in them a statement about the harness.
/// </para>
/// <para>
/// The families are <c>avares://</c> references, never bare names. Decision 0006 rules out naming a
/// family we do not ship: a bare name resolves against whatever the device happens to have
/// installed, so "one brand across desktop and mobile" silently becomes per-machine. The matching
/// files live under <c>Assets/Fonts</c> and are declared to the design token file, which generates
/// the same two names for Flutter.
/// </para>
/// </remarks>
internal static class AerFonts
{
    /// <summary>The prose face — must match <c>type.fontFamily.sans</c> in <c>design/tokens.json</c>.</summary>
    internal const string Sans = "avares://Aer.Ui/Assets/Fonts#Source Sans 3";

    /// <summary>The code face — must match <c>type.fontFamily.mono</c> in <c>design/tokens.json</c>.</summary>
    internal const string Mono = "avares://Aer.Ui/Assets/Fonts#JetBrains Mono";

    internal static AppBuilder WithAerFonts(this AppBuilder builder) =>
        builder.With(new FontManagerOptions { DefaultFamilyName = Sans });
}
