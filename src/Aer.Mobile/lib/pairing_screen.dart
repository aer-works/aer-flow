import 'package:flutter/material.dart';

import 'daemon/credentials_store.dart';
import 'daemon/daemon_client.dart';
import 'inbox_screen.dart';
import 'qr_scan_screen.dart';

/// Manual host+code entry, plus (M21 Phase 3, issue #234) a QR-scan shortcut against the desktop's
/// "Enable Remote Access" view — the payload is an `aer://pair?host=...&code=...` URI (decision of
/// record on the desktop side, `Aer.Ui.Core/RemoteViewModel.cs`); this screen just fills the same
/// two text fields a scan would have typed by hand, so a failed/garbled scan degrades to the manual
/// path with no dead end.
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

  Future<void> _scanQrCode() async {
    final rawValue = await Navigator.of(context).push<String>(
      MaterialPageRoute(builder: (_) => const QrScanScreen()),
    );
    if (rawValue == null) return;

    final uri = Uri.tryParse(rawValue);
    final host = uri?.queryParameters['host'];
    final code = uri?.queryParameters['code'];
    if (uri == null || uri.scheme != 'aer' || host == null || code == null) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('That QR code isn\'t an AER pairing code — enter the host and code by hand instead.')),
        );
      }
      return;
    }

    setState(() {
      _hostController.text = host;
      _codeController.text = code;
    });
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
                'On the desktop, open Aer Flow\'s Remote Access screen and turn on remote access — '
                'then scan its QR code, or enter the host and code shown there by hand.',
              ),
              const SizedBox(height: 16),
              OutlinedButton.icon(
                onPressed: _scanQrCode,
                icon: const Icon(Icons.qr_code_scanner),
                label: const Text('Scan QR code'),
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
