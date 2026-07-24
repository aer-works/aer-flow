using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Aer.DesignTokens;

/// <summary>
/// Renders <c>design/tokens.json</c> into each toolkit's theme resources (#345).
/// </summary>
/// <remarks>
/// <para>
/// Desktop is Avalonia and mobile is Flutter, and they share no styling primitive — so "one brand
/// across both" maintained by hand is two sources of truth that drift on the first change, the same
/// failure mode as the vocabulary map (#315) expressed in pixels. Generating both from one file makes
/// the drift a build failure instead of something a reviewer has to notice.
/// </para>
/// <para>
/// <b>Pure by design.</b> Generation is a string function of the parsed token file — no clock, no
/// environment, no filesystem reads beyond the input. That is what lets the CI gate regenerate in
/// memory and compare against the checked-in artifacts: a generator that varied with anything else
/// would make the gate flap and get disabled, which is exactly how a stale artifact survives.
/// </para>
/// <para>
/// <b>Emitted, not interpreted.</b> This renders whatever the token file says; it does not decide
/// design. If a value looks wrong, the fix is in <c>design/tokens.json</c>.
/// </para>
/// </remarks>
public static class TokenGenerator
{
    /// <summary>Both generated artifacts, keyed by repo-relative path.</summary>
    public static IReadOnlyDictionary<string, string> Generate(string tokensJson)
    {
        using var document = JsonDocument.Parse(tokensJson, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
        });

        var root = document.RootElement;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AvaloniaOutputPath] = GenerateAvalonia(root),
            [FlutterOutputPath] = GenerateFlutter(root),
        };
    }

    public const string TokensPath = "design/tokens.json";
    public const string AvaloniaOutputPath = "src/Aer.Ui/Theme/GeneratedTokens.axaml";
    public const string FlutterOutputPath = "src/Aer.Mobile/lib/theme/tokens.dart";

    /// <summary>The regeneration command, quoted in both the banner and the CI gate's failure text.</summary>
    public const string RegenerateCommand = "pixi run tokens";

    private const string BannerLine1 = "GENERATED FILE — DO NOT EDIT.";

    private static readonly string[] BannerBody =
    [
        BannerLine1,
        $"Source: {TokensPath}",
        $"Regenerate: {RegenerateCommand}",
        "",
        "Hand edits are reverted by the next regeneration and fail CI in the meantime",
        "(Aer.Architecture.Tests). Change the token file instead.",
    ];

    /// <summary>
    /// The do-not-edit header in the target language's comment syntax. Dart has no block comment in
    /// idiomatic use, so <paramref name="commentClose"/> being null selects per-line comments —
    /// emitting a block form into Dart would produce a file that does not parse.
    /// </summary>
    private static string Banner(string commentOpen, string? commentClose)
    {
        var banner = new StringBuilder();
        if (commentClose is null)
        {
            foreach (var line in BannerBody)
            {
                banner.AppendLine(line.Length == 0 ? commentOpen : $"{commentOpen} {line}");
            }

            return banner.ToString().TrimEnd();
        }

        banner.AppendLine(commentOpen);
        foreach (var line in BannerBody)
        {
            banner.AppendLine(line.Length == 0 ? string.Empty : $"    {line}");
        }

        banner.Append(commentClose);
        return banner.ToString();
    }

    // ---- colour helpers -------------------------------------------------------------------

    /// <summary>
    /// Every colour token carries a <c>light</c> and a <c>dark</c> value. Missing either is a
    /// malformed token file, not a default to invent — a silently substituted colour is precisely the
    /// stale-and-unchecked failure this pipeline exists to remove.
    /// </summary>
    private static (string Light, string Dark) Variants(JsonElement token, string path)
    {
        if (!token.TryGetProperty("light", out var light) || !token.TryGetProperty("dark", out var dark))
        {
            throw new InvalidOperationException(
                $"Colour token '{path}' must define both 'light' and 'dark'.");
        }

        return (light.GetString()!, dark.GetString()!);
    }

    private static IEnumerable<(string Name, JsonElement Value)> Entries(JsonElement parent) =>
        parent.EnumerateObject()
            .Where(property => !property.Name.StartsWith('$'))
            .Select(property => (property.Name, property.Value));

    /// <summary>
    /// A density block's numbers, flattening one level of nesting so <c>typeScale.title</c> emits as
    /// <c>TypeScaleTitle</c>. Flattened rather than skipped: the per-density type sizes are the
    /// difference between the two densities, so dropping them would silently generate a "density"
    /// that only changed padding.
    /// </summary>
    private static IEnumerable<(string Name, double Value)> DensityNumbers(JsonElement density)
    {
        foreach (var (name, value) in Entries(density))
        {
            if (value.ValueKind == JsonValueKind.Number)
            {
                yield return (Pascal(name), value.GetDouble());
                continue;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var (nestedName, nestedValue) in Entries(value))
                {
                    yield return (Pascal(name) + Pascal(nestedName), nestedValue.GetDouble());
                }
            }
        }
    }

    private static string Number(double value) =>
        value == Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Pascal(string camel) => char.ToUpperInvariant(camel[0]) + camel[1..];

    /// <summary>
    /// <c>#RRGGBB</c> as Dart's <c>0xFFRRGGBB</c>. Flutter has no hex-string colour literal, so the
    /// alpha channel has to be made explicit here rather than at every call site.
    /// </summary>
    private static string DartColor(string hex) => "0xFF" + hex.TrimStart('#').ToUpperInvariant();

    // ---- Avalonia -------------------------------------------------------------------------

    /// <summary>
    /// A <c>ResourceDictionary</c> with <c>ThemeDictionaries</c> for Light and Dark, which is what
    /// lets Avalonia follow the OS preference: the app sets <c>ThemeVariant.Default</c> and the
    /// correct set resolves per variant, so "system" needs no code of its own.
    /// </summary>
    private static string GenerateAvalonia(JsonElement root)
    {
        var light = new StringBuilder();
        var dark = new StringBuilder();

        void EmitColorGroup(string groupName, string prefix)
        {
            foreach (var (name, token) in Entries(root.GetProperty(groupName)))
            {
                var key = prefix + Pascal(name);
                if (groupName == "status")
                {
                    var (statusLight, statusDark) = Variants(token, $"{groupName}.{name}");
                    light.AppendLine($"""      <Color x:Key="{key}Color">{statusLight}</Color>""");
                    dark.AppendLine($"""      <Color x:Key="{key}Color">{statusDark}</Color>""");
                    continue;
                }

                var (lightValue, darkValue) = Variants(token, $"{groupName}.{name}");
                light.AppendLine($"""      <Color x:Key="{key}Color">{lightValue}</Color>""");
                dark.AppendLine($"""      <Color x:Key="{key}Color">{darkValue}</Color>""");
            }
        }

        EmitColorGroup("brand", "Brand");
        EmitColorGroup("surface", "Surface");
        EmitColorGroup("text", "Text");
        EmitColorGroup("status", "Status");

        var invariant = new StringBuilder();

        foreach (var (name, value) in Entries(root.GetProperty("radius")))
        {
            invariant.AppendLine($"""    <CornerRadius x:Key="Radius{Pascal(name)}">{Number(value.GetDouble())}</CornerRadius>""");
        }

        foreach (var (name, value) in Entries(root.GetProperty("spacing")))
        {
            invariant.AppendLine($"""    <sys:Double x:Key="Spacing{Pascal(name)}">{Number(value.GetDouble())}</sys:Double>""");
        }

        foreach (var (name, role) in Entries(root.GetProperty("type").GetProperty("role")))
        {
            invariant.AppendLine($"""    <sys:Double x:Key="FontSize{Pascal(name)}">{Number(role.GetProperty("size").GetDouble())}</sys:Double>""");
        }

        // Glyph and label travel with the colour deliberately: 0006 requires every status to read
        // without hue, so a surface that can reach the colour must be able to reach both of these.
        foreach (var (name, token) in Entries(root.GetProperty("status")))
        {
            invariant.AppendLine($"""    <sys:String x:Key="Status{Pascal(name)}Glyph">{token.GetProperty("glyph").GetString()}</sys:String>""");
            invariant.AppendLine($"""    <sys:String x:Key="Status{Pascal(name)}Label">{token.GetProperty("label").GetString()}</sys:String>""");
        }

        var desktop = root.GetProperty("density").GetProperty("desktop");
        foreach (var (name, value) in DensityNumbers(desktop))
        {
            invariant.AppendLine($"""    <sys:Double x:Key="Density{name}">{Number(value)}</sys:Double>""");
        }

        return $"""
        {Banner("<!--", "-->")}
        <ResourceDictionary xmlns="https://github.com/avaloniaui"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            xmlns:sys="clr-namespace:System;assembly=System.Runtime">
          <ResourceDictionary.ThemeDictionaries>
            <ResourceDictionary x:Key="Light">
        {light.ToString().TrimEnd()}
            </ResourceDictionary>
            <ResourceDictionary x:Key="Dark">
        {dark.ToString().TrimEnd()}
            </ResourceDictionary>
          </ResourceDictionary.ThemeDictionaries>

        {invariant.ToString().TrimEnd()}
        </ResourceDictionary>

        """.ReplaceLineEndings("\n");
    }

    // ---- Flutter --------------------------------------------------------------------------

    private static string GenerateFlutter(JsonElement root)
    {
        var colors = new StringBuilder();

        void EmitColorGroup(string groupName, string prefix)
        {
            foreach (var (name, token) in Entries(root.GetProperty(groupName)))
            {
                var (light, dark) = Variants(token, $"{groupName}.{name}");
                colors.AppendLine($"  static const Color {prefix}{Pascal(name)}Light = Color({DartColor(light)});");
                colors.AppendLine($"  static const Color {prefix}{Pascal(name)}Dark = Color({DartColor(dark)});");
            }
        }

        EmitColorGroup("brand", "brand");
        EmitColorGroup("surface", "surface");
        EmitColorGroup("text", "text");
        EmitColorGroup("status", "status");

        var scalars = new StringBuilder();
        foreach (var (name, value) in Entries(root.GetProperty("radius")))
        {
            scalars.AppendLine($"  static const double radius{Pascal(name)} = {Number(value.GetDouble())};");
        }

        foreach (var (name, value) in Entries(root.GetProperty("spacing")))
        {
            scalars.AppendLine($"  static const double spacing{Pascal(name)} = {Number(value.GetDouble())};");
        }

        foreach (var (name, role) in Entries(root.GetProperty("type").GetProperty("role")))
        {
            scalars.AppendLine($"  static const double fontSize{Pascal(name)} = {Number(role.GetProperty("size").GetDouble())};");
        }

        var mobile = root.GetProperty("density").GetProperty("mobile");
        foreach (var (name, value) in DensityNumbers(mobile))
        {
            scalars.AppendLine($"  static const double density{name} = {Number(value)};");
        }

        var statusEnum = new StringBuilder();
        var statusGlyph = new StringBuilder();
        var statusLabel = new StringBuilder();
        var statusLight = new StringBuilder();
        var statusDark = new StringBuilder();

        foreach (var (name, token) in Entries(root.GetProperty("status")))
        {
            statusEnum.AppendLine($"  {name},");
            statusGlyph.AppendLine($"        AerStatus.{name} => '{token.GetProperty("glyph").GetString()}',");
            statusLabel.AppendLine($"        AerStatus.{name} => '{token.GetProperty("label").GetString()}',");
            statusLight.AppendLine($"        AerStatus.{name} => AerTokens.status{Pascal(name)}Light,");
            statusDark.AppendLine($"        AerStatus.{name} => AerTokens.status{Pascal(name)}Dark,");
        }

        var motion = root.GetProperty("motion");

        return $$"""
        {{Banner("//", null)}}
        import 'package:flutter/material.dart';

        /// Raw token values. Prefer [aerTheme] over reaching for these directly.
        class AerTokens {
        {{colors.ToString().TrimEnd()}}

        {{scalars.ToString().TrimEnd()}}

          static const Duration durationQuick = Duration(milliseconds: {{Number(motion.GetProperty("durationQuickMs").GetDouble())}});
          static const Duration durationStandard = Duration(milliseconds: {{Number(motion.GetProperty("durationStandardMs").GetDouble())}});
        }

        /// The five states from #334's split.
        enum AerStatus {
        {{statusEnum.ToString().TrimEnd()}}
        }

        /// Decision 0006: a status must never be conveyed by hue alone, so every state carries a
        /// glyph and a word. Render [glyph] and [label] together - colour is the third channel, not
        /// the only one.
        extension AerStatusPresentation on AerStatus {
          String get glyph => switch (this) {
        {{statusGlyph.ToString().TrimEnd()}}
              };

          String get label => switch (this) {
        {{statusLabel.ToString().TrimEnd()}}
              };

          Color color(Brightness brightness) => brightness == Brightness.dark
              ? switch (this) {
        {{statusDark.ToString().TrimEnd()}}
                }
              : switch (this) {
        {{statusLight.ToString().TrimEnd()}}
                };
        }

        /// Builds [ThemeData] for one brightness. Pass both to `MaterialApp(theme:, darkTheme:)` with
        /// `themeMode: ThemeMode.system` - that is the whole of "system" support; Flutter resolves the
        /// OS preference itself once both are supplied.
        ThemeData aerTheme(Brightness brightness) {
          final isDark = brightness == Brightness.dark;
          final accent = isDark ? AerTokens.brandAccentDark : AerTokens.brandAccentLight;
          final onAccent = isDark ? AerTokens.brandOnAccentDark : AerTokens.brandOnAccentLight;
          final ground = isDark ? AerTokens.surfaceGroundDark : AerTokens.surfaceGroundLight;
          final raised = isDark ? AerTokens.surfaceRaisedDark : AerTokens.surfaceRaisedLight;
          final rule = isDark ? AerTokens.surfaceRuleDark : AerTokens.surfaceRuleLight;
          final primary = isDark ? AerTokens.textPrimaryDark : AerTokens.textPrimaryLight;
          final secondary = isDark ? AerTokens.textSecondaryDark : AerTokens.textSecondaryLight;

          return ThemeData(
            brightness: brightness,
            scaffoldBackgroundColor: ground,
            colorScheme: ColorScheme.fromSeed(
              seedColor: accent,
              brightness: brightness,
            ).copyWith(
              primary: accent,
              onPrimary: onAccent,
              surface: raised,
              onSurface: primary,
              outline: rule,
            ),
            dividerColor: rule,
            textTheme: TextTheme(
              titleMedium: TextStyle(
                fontSize: AerTokens.densityTypeScaleTitle,
                fontWeight: FontWeight.w600,
                color: primary,
              ),
              bodyMedium: TextStyle(fontSize: AerTokens.fontSizeBody, color: primary),
              bodySmall: TextStyle(
                fontSize: AerTokens.densityTypeScaleSecondary,
                color: secondary,
              ),
            ),
          );
        }

        """.ReplaceLineEndings(Lf);
    }

    /// <summary>
    /// Generated artifacts are always LF, on every platform. Otherwise the CI gate would compare a
    /// CRLF regeneration on Windows against an LF file checked in from Linux and fail on line
    /// endings alone — a gate that fires on nothing real is a gate that gets turned off.
    /// </summary>
    private const string Lf = "\n";
}
