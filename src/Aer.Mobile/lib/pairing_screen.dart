import 'package:flutter/material.dart';
import 'package:http/http.dart' as http;
import 'package:tailscale/tailscale.dart';
import 'package:url_launcher/url_launcher.dart';

import 'daemon/credentials_store.dart';
import 'daemon/daemon_client.dart';
import 'daemon/tailnet_gateway.dart';
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
  String? _statusText;
  final _tailnet = TailnetGateway();

  @override
  void dispose() {
    _hostController.dispose();
    _codeController.dispose();
    _clientNameController.dispose();
    super.dispose();
  }

  Future<void> _scanQrCode() async {
    final rawValue = await Navigator.of(
      context,
    ).push<String>(MaterialPageRoute(builder: (_) => const QrScanScreen()));
    if (rawValue == null) return;

    final uri = Uri.tryParse(rawValue);
    final host = uri?.queryParameters['host'];
    final code = uri?.queryParameters['code'];
    if (uri == null || uri.scheme != 'aer' || host == null || code == null) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(
            content: Text(
              'That QR code isn\'t an AER pairing code — enter the host and code by hand instead.',
            ),
          ),
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
      _statusText = null;
    });

    final host = _hostController.text.trim();
    final tailnetRouted = isTailnetHost(host);
    try {
      // A tailnet-only host (M21 Phase 7, #246) isn't reachable over the phone's regular network
      // stack at all — tsnet is userspace-only, with no OS-level route for it — so pairing itself
      // has to go over the embedded node's own http.client, not just later REST calls.
      final httpClient = tailnetRouted ? await _joinTailnet() : null;
      if (tailnetRouted) {
        setState(() => _statusText = 'Connected — checking the sidecar...');
        await DaemonClient.version(host: host, httpClient: httpClient);
      }

      final token = await DaemonClient.pair(
        host: host,
        code: _codeController.text.trim(),
        clientName: _clientNameController.text.trim(),
        httpClient: httpClient,
      );
      await CredentialsStore().save(
        DaemonCredentials(host: host, token: token, tsnetRouted: tailnetRouted),
      );
      if (!mounted) return;
      Navigator.of(
        context,
      ).pushReplacement(MaterialPageRoute(builder: (_) => const InboxScreen()));
    } on DaemonException catch (e) {
      setState(() => _errorText = e.message);
    } catch (e) {
      setState(
        () => _errorText =
            'Could not reach $host — check the host and that Aer.Daemon is running with --remote.',
      );
    } finally {
      if (mounted) {
        setState(() {
          _isPairing = false;
          _statusText = null;
        });
      }
    }
  }

  /// Brings the embedded tailnet node up, driving the phone through a one-time interactive
  /// sign-in on first launch (mirrors the desktop sidecar's own "Sign in to Tailscale" button).
  /// Later launches reconnect from persisted credentials with no browser step.
  Future<http.Client> _joinTailnet() async {
    setState(() => _statusText = 'Connecting to Tailscale...');
    final status = await _tailnet.ensureUp();

    if (status.state == NodeState.needsMachineAuth) {
      throw DaemonException(
        'This phone needs admin approval in the Tailscale admin console before it can join — see https://tailscale.com/kb/1099/device-approval.',
      );
    }

    if (status.needsLogin) {
      final authUrl = status.authUrl;
      if (authUrl == null) {
        throw DaemonException(
          'Tailscale needs sign-in but returned no login URL.',
        );
      }
      setState(
        () => _statusText =
            'Sign in to Tailscale in the browser that just opened...',
      );
      final opened = await launchUrl(
        authUrl,
        mode: LaunchMode.externalApplication,
      );
      if (!opened) {
        throw DaemonException(
          'Could not open the Tailscale sign-in page ($authUrl).',
        );
      }
      await _tailnet.waitUntilRunning();
    }

    return _tailnet.client;
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
                decoration: const InputDecoration(
                  labelText: 'Host',
                  hintText: '192.168.1.23:5050',
                ),
                validator: (value) =>
                    (value == null || value.trim().isEmpty) ? 'Required' : null,
              ),
              const SizedBox(height: 12),
              TextFormField(
                controller: _codeController,
                decoration: const InputDecoration(
                  labelText: 'Pairing code',
                  hintText: '6 digits, expires in 60s',
                ),
                keyboardType: TextInputType.number,
                validator: (value) =>
                    (value == null || value.trim().isEmpty) ? 'Required' : null,
              ),
              const SizedBox(height: 12),
              TextFormField(
                controller: _clientNameController,
                decoration: const InputDecoration(
                  labelText: 'This device\'s name',
                ),
                validator: (value) =>
                    (value == null || value.trim().isEmpty) ? 'Required' : null,
              ),
              const SizedBox(height: 24),
              if (_statusText != null) ...[
                Text(
                  _statusText!,
                  style: Theme.of(context).textTheme.bodySmall,
                ),
                const SizedBox(height: 12),
              ],
              if (_errorText != null) ...[
                Text(
                  _errorText!,
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
                ),
                const SizedBox(height: 12),
              ],
              FilledButton(
                onPressed: _isPairing ? null : _pair,
                child: _isPairing
                    ? const CircularProgressIndicator()
                    : const Text('Pair'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
