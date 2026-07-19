import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:aer_mobile/pairing_screen.dart';

void main() {
  // PairingScreen only, not AerMobileApp: the app's startup router reads
  // flutter_secure_storage on initState, which has no platform channel binding in
  // `flutter test`'s headless environment. PairingScreen itself touches storage only
  // once the user taps Pair, so it's safe to pump directly.
  testWidgets('Pairing screen shows the host, code, and device name fields', (WidgetTester tester) async {
    await tester.pumpWidget(const MaterialApp(home: PairingScreen()));

    expect(find.text('Host'), findsOneWidget);
    expect(find.text('Pairing code'), findsOneWidget);
    expect(find.text('Pair'), findsOneWidget);
  });
}
