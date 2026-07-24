// GENERATED FILE — DO NOT EDIT.
// Source: design/tokens.json
// Regenerate: pixi run tokens
//
// Hand edits are reverted by the next regeneration and fail CI in the meantime
// (Aer.Architecture.Tests). Change the token file instead.
import 'package:flutter/material.dart';

/// Raw token values. Prefer [aerTheme] over reaching for these directly.
class AerTokens {
  static const Color brandAccentLight = Color(0xFF3F8C87);
  static const Color brandAccentDark = Color(0xFF5FB3AD);
  static const Color brandOnAccentLight = Color(0xFFFFFFFF);
  static const Color brandOnAccentDark = Color(0xFF0B1112);
  static const Color surfaceGroundLight = Color(0xFFF2F4F5);
  static const Color surfaceGroundDark = Color(0xFF14191B);
  static const Color surfaceRaisedLight = Color(0xFFFFFFFF);
  static const Color surfaceRaisedDark = Color(0xFF1B2124);
  static const Color surfaceSunkLight = Color(0xFFE7EBEC);
  static const Color surfaceSunkDark = Color(0xFF101517);
  static const Color surfaceRuleLight = Color(0xFFD6DDDF);
  static const Color surfaceRuleDark = Color(0xFF2A3236);
  static const Color textPrimaryLight = Color(0xFF1B2226);
  static const Color textPrimaryDark = Color(0xFFDCE4E6);
  static const Color textSecondaryLight = Color(0xFF5A676D);
  static const Color textSecondaryDark = Color(0xFF94A3A8);
  static const Color textMutedLight = Color(0xFF88969C);
  static const Color textMutedDark = Color(0xFF6C7A80);
  static const Color statusWorkingLight = Color(0xFF1F6FA8);
  static const Color statusWorkingDark = Color(0xFF5AB0E0);
  static const Color statusNeedsInputLight = Color(0xFF8F5E0E);
  static const Color statusNeedsInputDark = Color(0xFFDFA84E);
  static const Color statusReadyForReviewLight = Color(0xFF63489E);
  static const Color statusReadyForReviewDark = Color(0xFFA88CDE);
  static const Color statusFinishedLight = Color(0xFF2C6E4C);
  static const Color statusFinishedDark = Color(0xFF6DBE94);
  static const Color statusFailedLight = Color(0xFF9E382C);
  static const Color statusFailedDark = Color(0xFFE08274);

  static const double radiusSm = 3;
  static const double radiusMd = 5;
  static const double radiusLg = 7;
  static const double radiusXl = 9;
  static const double radiusPanel = 10;
  static const double radiusPill = 999;
  static const double spacingXs = 3;
  static const double spacingSm = 6;
  static const double spacingMd = 10;
  static const double spacingLg = 16;
  static const double spacingXl = 20;
  static const double spacingXxl = 28;
  static const double fontSizeTitle = 13.5;
  static const double fontSizeBody = 13;
  static const double fontSizeSecondary = 12;
  static const double fontSizeAction = 12.5;
  static const double fontSizeStatus = 12;
  static const double fontSizeMeta = 10.5;
  static const double fontSizeLabel = 9.5;
  static const double fontSizeCode = 11.5;
  static const double densityRowPaddingX = 12;
  static const double densityRowPaddingY = 13;
  static const double densityRowGap = 3;
  static const double densityMinTouchTarget = 48;
  static const double densityTypeScaleTitle = 15;
  static const double densityTypeScaleSecondary = 13.5;

  static const Duration durationQuick = Duration(milliseconds: 120);
  static const Duration durationStandard = Duration(milliseconds: 200);
}

/// The five states from #334's split.
enum AerStatus {
  working,
  needsInput,
  readyForReview,
  finished,
  failed,
}

/// Decision 0006: a status must never be conveyed by hue alone, so every state carries a
/// glyph and a word. Render [glyph] and [label] together - colour is the third channel, not
/// the only one.
extension AerStatusPresentation on AerStatus {
  String get glyph => switch (this) {
        AerStatus.working => '◐',
        AerStatus.needsInput => '◆',
        AerStatus.readyForReview => '▣',
        AerStatus.finished => '✓',
        AerStatus.failed => '✕',
      };

  String get label => switch (this) {
        AerStatus.working => 'Working',
        AerStatus.needsInput => 'Needs input',
        AerStatus.readyForReview => 'Ready for review',
        AerStatus.finished => 'Finished',
        AerStatus.failed => 'Failed',
      };

  Color color(Brightness brightness) => brightness == Brightness.dark
      ? switch (this) {
        AerStatus.working => AerTokens.statusWorkingDark,
        AerStatus.needsInput => AerTokens.statusNeedsInputDark,
        AerStatus.readyForReview => AerTokens.statusReadyForReviewDark,
        AerStatus.finished => AerTokens.statusFinishedDark,
        AerStatus.failed => AerTokens.statusFailedDark,
        }
      : switch (this) {
        AerStatus.working => AerTokens.statusWorkingLight,
        AerStatus.needsInput => AerTokens.statusNeedsInputLight,
        AerStatus.readyForReview => AerTokens.statusReadyForReviewLight,
        AerStatus.finished => AerTokens.statusFinishedLight,
        AerStatus.failed => AerTokens.statusFailedLight,
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
