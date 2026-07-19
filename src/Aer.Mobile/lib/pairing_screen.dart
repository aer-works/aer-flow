import 'package:flutter/material.dart';

import 'daemon/credentials_store.dart';
import 'daemon/daemon_client.dart';
import 'inbox_screen.dart';

/// Manual host+code entry only for Phase 2 — QR scanning is real scope for this screen (the phone
/// only ever *scans*, it doesn't need the desktop's QR generator to exist first to write this UI),
/// but there's nothing to scan against until Phase 3 adds the desktop's "Enable Remote Access" view,
/// so it stays out until there's something real to test it against.
class PairingScreen extends StatefulWidget {
  const PairingScreen({super.key});

  @override
  State<PairingScreen> createState() => _PairingScreenState();
}

class _PairingScreenState extends State<PairingScreen> {
  final _formKey = GlobalKey<FormState>();
  final _hostController = TextEditingController();
  final _codeController = TextEditingController();
  final _clientNameController = TextEditingController(text: 'My Phone');

  bool _isPairing = false;
  String? _errorText;

  @override
  void dispose() {
    _hostController.dispose();
    _codeController.dispose();
    _clientNameController.dispose();
    super.dispose();
  }

  Future<void> _pair() async {
    if (!_formKey.currentState!.validate()) return;

    setState(() {
      _isPairing = true;
      _errorText = null;
    });

    final host = _hostController.text.trim();
    try {
      final token = await DaemonClient.pair(
        host: host,
        code: _codeController.text.trim(),
        clientName: _clientNameController.text.trim(),
      );
      await CredentialsStore().save(DaemonCredentials(host: host, token: token));
      if (!mounted) return;
      Navigator.of(context).pushReplacement(MaterialPageRoute(builder: (_) => const InboxScreen()));
    } on DaemonException catch (e) {
      setState(() => _errorText = e.message);
    } catch (e) {
      setState(() => _errorText = 'Could not reach $host — check the host and that Aer.Daemon is running with --remote.');
    } finally {
      if (mounted) setState(() => _isPairing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Pair with Aer')),
      body: Padding(
        padding: const EdgeInsets.all(24),
        child: Form(
          key: _formKey,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const Text(
                'On the desktop, run Aer.Daemon with --remote, then get a pairing code '
                '(GET /api/pairing/code until Phase 3 adds a screen for this).',
              ),
              const SizedBox(height: 24),
              TextFormField(
                controller: _hostController,
                decoration: const InputDecoration(labelText: 'Host', hintText: '192.168.1.23:5050'),
                validator: (value) => (value == null || value.trim().isEmpty) ? 'Required' : null,
              ),
              const SizedBox(height: 12),
              TextFormField(
                controller: _codeController,
                decoration: const InputDecoration(labelText: 'Pairing code', hintText: '6 digits, expires in 60s'),
                keyboardType: TextInputType.number,
                validator: (value) => (value == null || value.trim().isEmpty) ? 'Required' : null,
              ),
              const SizedBox(height: 12),
              TextFormField(
                controller: _clientNameController,
                decoration: const InputDecoration(labelText: 'This device\'s name'),
                validator: (value) => (value == null || value.trim().isEmpty) ? 'Required' : null,
              ),
              const SizedBox(height: 24),
              if (_errorText != null) ...[
                Text(_errorText!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
                const SizedBox(height: 12),
              ],
              FilledButton(
                onPressed: _isPairing ? null : _pair,
                child: _isPairing ? const CircularProgressIndicator() : const Text('Pair'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
