import 'package:flutter/material.dart';

import 'daemon/credentials_store.dart';
import 'daemon/tailnet_gateway.dart';
import 'inbox_screen.dart';
import 'pairing_screen.dart';

void main() async {
  WidgetsFlutterBinding.ensureInitialized();
  await TailnetGateway.init();
  runApp(const AerMobileApp());
}

class AerMobileApp extends StatelessWidget {
  const AerMobileApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'AER Flow',
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(seedColor: Colors.deepPurple),
      ),
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
