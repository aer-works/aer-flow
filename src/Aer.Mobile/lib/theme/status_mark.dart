import 'dart:math' as math;

import 'package:flutter/material.dart';

import 'tokens.dart';

/// Draws a status's mark (#458).
///
/// Decision 0006 requires status to read without colour, and the original mechanism — a Unicode
/// character per state — cannot deliver that here: three of the five codepoints are absent from
/// Source Sans 3, one from JetBrains Mono, and between them the two shipped faces carry no
/// checkmark and no cross at all. A codepoint a font lacks renders as tofu or falls back to
/// whatever the device happens to have, which is the per-device resolution 0006 exists to rule out
/// — arriving on the one element the accessibility rule depends on.
///
/// So `design/tokens.json` names a *shape* and each toolkit draws it. These coordinates are
/// authored on the same 16x16 grid as `Aer.Ui/Theme/Icons.axaml` and match its `Icon.Ring`,
/// `Icon.Diamond`, `Icon.Page`, `Icon.Check` and `Icon.Cross` point for point, following the
/// precedent `_VendorGlyphPainter` in `inbox_screen.dart` already set for cross-toolkit shapes.
/// `Aer.Architecture.Tests` fails the build if a status names a mark this file does not handle.
///
/// The five differ in silhouette, not merely in colour: round-open, angular-solid,
/// tall-rectangular, angular-line, angular-X. Five marks that could only be told apart once you
/// could see their colour would satisfy a literal reading of the rule and fail the people it is for.
class StatusMark extends StatelessWidget {
  const StatusMark(this.status, {super.key, this.size = 16.0, this.color});

  final AerStatus status;
  final double size;

  /// Defaults to the status's own colour for the ambient brightness. Pass a colour to render the
  /// mark against a surface that already carries the status hue, where repeating it would vanish.
  final Color? color;

  @override
  Widget build(BuildContext context) {
    final resolved = color ?? status.color(Theme.of(context).brightness);
    return Semantics(
      label: status.label,
      child: CustomPaint(
        size: Size(size, size),
        painter: _StatusMarkPainter(mark: status.mark, color: resolved),
      ),
    );
  }
}

class _StatusMarkPainter extends CustomPainter {
  const _StatusMarkPainter({required this.mark, required this.color});

  final String mark;
  final Color color;

  /// The grid these coordinates are authored on, shared with `Icons.axaml`.
  static const double _grid = 16.0;

  /// Proportional to the grid so the stroke keeps its weight when the mark is scaled.
  static const double _strokeOnGrid = 1.6;

  @override
  void paint(Canvas canvas, Size size) {
    final scale = size.shortestSide / _grid;
    Offset at(double x, double y) => Offset(x * scale, y * scale);

    final stroke = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = _strokeOnGrid * scale
      ..strokeCap = StrokeCap.round
      ..strokeJoin = StrokeJoin.round;

    final fill = Paint()
      ..color = color
      ..style = PaintingStyle.fill;

    switch (mark) {
      // An open ring — the static frame of a spinner. Matches Icon.Ring's arc: centred at (8,8)
      // with radius 5, starting at the top and sweeping 240 degrees, leaving the gap that stops it
      // reading as a plain circle.
      case 'ring':
        canvas.drawArc(
          Rect.fromCircle(center: at(8, 8), radius: 5 * scale),
          -math.pi / 2,
          240 * math.pi / 180,
          false,
          stroke,
        );
      case 'diamond':
        canvas.drawPath(
          Path()..addPolygon([at(8, 2.5), at(13.5, 8), at(8, 13.5), at(2.5, 8)], true),
          fill,
        );
      // A page with a turned corner: the outline, then the fold.
      case 'page':
        canvas.drawPath(
          Path()..addPolygon([at(4, 2.5), at(9.5, 2.5), at(12, 5), at(12, 13.5), at(4, 13.5)], true),
          stroke,
        );
        canvas.drawPath(
          Path()
            ..moveTo(at(9.5, 2.5).dx, at(9.5, 2.5).dy)
            ..lineTo(at(9.5, 5).dx, at(9.5, 5).dy)
            ..lineTo(at(12, 5).dx, at(12, 5).dy),
          stroke,
        );
      case 'check':
        canvas.drawPath(
          Path()
            ..moveTo(at(3, 8.5).dx, at(3, 8.5).dy)
            ..lineTo(at(6.5, 12).dx, at(6.5, 12).dy)
            ..lineTo(at(13, 4).dx, at(13, 4).dy),
          stroke,
        );
      case 'cross':
        canvas.drawLine(at(4, 4), at(12, 12), stroke);
        canvas.drawLine(at(12, 4), at(4, 12), stroke);
      default:
        // Never silently draw nothing: a status whose mark this file does not know would otherwise
        // render as empty space and read as "no status" rather than as a bug. The drift gate makes
        // this unreachable in a built tree; this is what happens if it is ever bypassed.
        throw ArgumentError.value(mark, 'mark', 'No painter for this status mark');
    }
  }

  @override
  bool shouldRepaint(_StatusMarkPainter oldDelegate) =>
      oldDelegate.mark != mark || oldDelegate.color != color;
}
