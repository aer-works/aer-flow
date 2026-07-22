import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:tailscale/tailscale.dart';

import 'package:aer_mobile/daemon/daemon_client.dart';

/// Regression coverage for issue #287: on Android, the embedded tsnet node only ever got started
/// once, from the one-time pairing flow — any later process (e.g. a relaunch after Android
/// reclaimed the app while backgrounded) dialed straight into a never-started `Tailscale.instance`
/// and failed immediately with `TailscaleTcpException(tcp): tcpDialFd called before Start`, with no
/// working recovery short of a full app restart. `DaemonClient` now brings the node up (or confirms
/// it's already up) before every tsnet dial, so both the initial connect and the Reconnect button
/// self-heal instead of retrying against a client already known to be dead.
void main() {
  group('DaemonClient tsnet lifecycle (issue #287)', () {
    test('watch() ensures the tsnet node is up before dialing', () async {
      var ensureUpCalls = 0;
      var dialCalls = 0;

      final client = DaemonClient(
        host: '100.64.0.1:5050',
        token: 'fake-token',
        tsnetRouted: true,
        tsnetEnsureUpFn: () async {
          ensureUpCalls++;
          return const TailscaleStatus(state: NodeState.running, tailscaleIPs: [], health: []);
        },
        tsnetDialFn: (host, port) {
          dialCalls++;
          expect(host, '100.64.0.1');
          expect(port, 5050);
          // The handshake itself isn't under test here -- failing fast after the call proves
          // ensureUp ran (and succeeded) strictly before any dial was attempted.
          throw StateError('dial reached (expected once ensureUp reports running)');
        },
      );

      final errors = <Object>[];
      final done = Completer<void>();
      client.watch().listen(
        (_) {},
        onError: (Object e) => errors.add(e),
        onDone: done.complete,
      );
      await done.future;

      expect(ensureUpCalls, 1);
      expect(dialCalls, 1);
      expect(errors, hasLength(1));
      expect(errors.single, isA<StateError>());
    });

    test('watch() never dials when the tsnet node cannot be brought up', () async {
      var ensureUpCalls = 0;
      var dialCalls = 0;

      final client = DaemonClient(
        host: '100.64.0.1:5050',
        token: 'fake-token',
        tsnetRouted: true,
        tsnetEnsureUpFn: () async {
          ensureUpCalls++;
          // Fresh process, persisted credentials exist but up() hasn't run yet -- this is exactly
          // the state a relaunched process reports before this fix (see NodeState.stopped's doc
          // comment in the vendored tailscale package).
          return TailscaleStatus.stopped;
        },
        tsnetDialFn: (host, port) {
          dialCalls++;
          throw StateError('dial should never be reached when the node is not running');
        },
      );

      final errors = <Object>[];
      final done = Completer<void>();
      client.watch().listen(
        (_) {},
        onError: (Object e) => errors.add(e),
        onDone: done.complete,
      );
      await done.future;

      expect(ensureUpCalls, 1);
      expect(dialCalls, 0);
      expect(errors, hasLength(1));
      expect(errors.single, isA<DaemonException>());
      expect((errors.single as DaemonException).message, contains('Tailscale is not connected'));
      expect((errors.single as DaemonException).message, contains('stopped'));
    });

    test('watchProgress() also ensures the tsnet node is up before dialing', () async {
      var ensureUpCalls = 0;
      var dialCalls = 0;

      final client = DaemonClient(
        host: '100.64.0.1:5050',
        token: 'fake-token',
        tsnetRouted: true,
        tsnetEnsureUpFn: () async {
          ensureUpCalls++;
          return TailscaleStatus.stopped;
        },
        tsnetDialFn: (host, port) {
          dialCalls++;
          throw StateError('dial should never be reached when the node is not running');
        },
      );

      final errors = <Object>[];
      final done = Completer<void>();
      client.watchProgress().listen(
        (_) {},
        onError: (Object e) => errors.add(e),
        onDone: done.complete,
      );
      await done.future;

      expect(ensureUpCalls, 1);
      expect(dialCalls, 0);
      expect(errors, hasLength(1));
      expect(errors.single, isA<DaemonException>());
    });

    test('Reconnect re-runs ensureUp on every attempt rather than retrying a known-dead client', () async {
      // Simulates tapping Reconnect against a node that only comes up on the second attempt --
      // e.g. the first resume races the control plane, or (pre-fix) the button did nothing because
      // it never tried to bring the engine up at all.
      var ensureUpCalls = 0;
      var dialCalls = 0;

      DaemonClient makeClient() => DaemonClient(
        host: '100.64.0.1:5050',
        token: 'fake-token',
        tsnetRouted: true,
        tsnetEnsureUpFn: () async {
          ensureUpCalls++;
          if (ensureUpCalls < 2) return TailscaleStatus.stopped;
          return const TailscaleStatus(state: NodeState.running, tailscaleIPs: [], health: []);
        },
        tsnetDialFn: (host, port) {
          dialCalls++;
          throw StateError('reached dial');
        },
      );

      // First attempt (e.g. the initial connect): node isn't up yet, no dial attempted.
      final firstErrors = <Object>[];
      final firstDone = Completer<void>();
      makeClient().watch().listen((_) {}, onError: (Object e) => firstErrors.add(e), onDone: firstDone.complete);
      await firstDone.future;
      expect(firstErrors.single, isA<DaemonException>());
      expect(dialCalls, 0);

      // Tapping Reconnect re-runs the same watch() path -- ensureUp succeeds this time, and now a
      // dial is actually attempted instead of the button silently doing nothing.
      final secondErrors = <Object>[];
      final secondDone = Completer<void>();
      makeClient().watch().listen((_) {}, onError: (Object e) => secondErrors.add(e), onDone: secondDone.complete);
      await secondDone.future;
      expect(secondErrors.single, isA<StateError>());
      expect(dialCalls, 1);
      expect(ensureUpCalls, 2);
    });
  });
}
