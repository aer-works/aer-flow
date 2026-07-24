import 'package:flutter/material.dart';

import 'daemon/credentials_store.dart';
import 'daemon/tailnet_gateway.dart';
import 'inbox_screen.dart';
import 'pairing_screen.dart';
import 'theme/tokens.dart';

void main() async {
  WidgetsFlutterBinding.ensureInitialized();
  await TailnetGateway.init();
  runApp(const AerMobileApp());
}

class AerMobileApp extends StatelessWidget {
  const AerMobileApp({super.key});

  @override
  Widget build(BuildContext context) {
    // #456: the generated theme replaces the Flutter starter's deepPurple seed, which was never a
    // design decision — it is what `flutter create` writes. Supplying both brightnesses is the
    // whole of "system" support: ThemeMode.system resolves the OS preference itself, so the three
    // modes decision 0006 asks for need no code of ours.
    return MaterialApp(
      title: 'AER Flow',
      theme: aerTheme(Brightness.light),
      darkTheme: aerTheme(Brightness.dark),
      themeMode: ThemeMode.system,
      home: const _StartupRouter(),
    );
  }
}

/// Skips the pairing screen entirely if this device already has stored credentials — pairing is a
/// one-time setup, not something to repeat every launch.
class _StartupRouter extends StatefulWidget {
  const _StartupRouter();

  @override
  State<_StartupRouter> createState() => _StartupRouterState();
}

class _StartupRouterState extends State<_StartupRouter> {
  bool? _isPaired;

  @override
  void initState() {
    super.initState();
    CredentialsStore().load().then((credentials) {
      if (mounted) setState(() => _isPaired = credentials != null);
    });
  }

  @override
  Widget build(BuildContext context) {
    if (_isPaired == null) {
      return const Scaffold(body: Center(child: CircularProgressIndicator()));
    }
    return _isPaired! ? const InboxScreen() : const PairingScreen();
  }
}
