import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';

import 'package:aer_mobile/daemon/ws_client.dart';
import 'package:aer_mobile/daemon/ws_codec.dart';

class MockWsSocket implements WsSocket {
  final StreamController<Uint8List> _inputController =
      StreamController<Uint8List>();
  final List<Uint8List> sentBytes = [];
  bool isClosed = false;

  @override
  Stream<Uint8List> get input => _inputController.stream;

  void pushInput(Uint8List chunk) => _inputController.add(chunk);

  @override
  Future<void> write(List<int> bytes) async {
    sentBytes.add(Uint8List.fromList(bytes));
  }

  @override
  Future<void> close() async {
    isClosed = true;
    await _inputController.close();
  }
}

void main() {
  group('RFC 6455 SHA-1 & Handshake', () {
    test('computeSecWebSocketAccept matches RFC 6455 official test vector', () {
      const sampleKey = 'dGhlIHNhbXBsZSBub25jZQ==';
      const expectedAccept = 's3pPLMBiTxaQ9kYGzzhZRbK+xOo=';
      expect(computeSecWebSocketAccept(sampleKey), expectedAccept);
    });

    test('buildHandshakeRequest formats valid GET request', () {
      final req = buildHandshakeRequest(
        host: '100.64.1.2:5050',
        path: '/api/ws?token=test-token',
        secWebSocketKey: 'sampleKey==',
      );
      expect(req, contains('GET /api/ws?token=test-token HTTP/1.1\r\n'));
      expect(req, contains('Host: 100.64.1.2:5050\r\n'));
      expect(req, contains('Upgrade: websocket\r\n'));
      expect(req, contains('Connection: Upgrade\r\n'));
      expect(req, contains('Sec-WebSocket-Key: sampleKey==\r\n'));
      expect(req, contains('Sec-WebSocket-Version: 13\r\n\r\n'));
    });

    test('verifyHandshakeResponse passes for valid 101 response', () {
      const key = 'dGhlIHNhbXBsZSBub25jZQ==';
      final acceptKey = computeSecWebSocketAccept(key);
      final validResponse =
          'HTTP/1.1 101 Switching Protocols\r\n'
          'Upgrade: websocket\r\n'
          'Connection: Upgrade\r\n'
          'Sec-WebSocket-Accept: $acceptKey\r\n\r\n';

      expect(() => verifyHandshakeResponse(validResponse, acceptKey), returnsNormally);
    });

    test('verifyHandshakeResponse throws on non-101 status code', () {
      const response =
          'HTTP/1.1 401 Unauthorized\r\n'
          'Content-Type: text/plain\r\n\r\n';
      expect(
        () => verifyHandshakeResponse(response, 'anyKey'),
        throwsA(isA<FormatException>()),
      );
    });

    test('verifyHandshakeResponse throws on accept key mismatch', () {
      const response =
          'HTTP/1.1 101 Switching Protocols\r\n'
          'Upgrade: websocket\r\n'
          'Connection: Upgrade\r\n'
          'Sec-WebSocket-Accept: wrongKey\r\n\r\n';
      expect(
        () => verifyHandshakeResponse(response, 'expectedKey'),
        throwsA(isA<FormatException>()),
      );
    });
  });

  group('RFC 6455 Frame Encoding & Decoding', () {
    test('encodeWsFrame & WsStreamParser round-trip small masked payload (<126 bytes)', () {
      final payload = utf8.encode('Hello, AER Flow WebSocket!');
      final frameBytes = encodeWsFrame(
        payload,
        opcode: 0x1,
        fin: true,
        masked: true,
        maskKey: [10, 20, 30, 40],
      );

      final parser = WsStreamParser();
      // Simulate handshake already done
      parser.addBytes(utf8.encode('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: x\r\n\r\n'));
      parser.setExpectedAcceptKey('x');
      expect(parser.tryParseHandshake(), isTrue);

      parser.addBytes(frameBytes);
      final decoded = parser.tryParseFrame();
      expect(decoded, isNotNull);
      expect(decoded!.fin, isTrue);
      expect(decoded.opcode, 0x1);
      expect(utf8.decode(decoded.payload), 'Hello, AER Flow WebSocket!');
    });

    test('encodeWsFrame & WsStreamParser round-trip medium payload (126-65535 bytes)', () {
      final text = 'A' * 300;
      final payload = utf8.encode(text);
      final frameBytes = encodeWsFrame(payload, opcode: 0x1, fin: true, masked: true);

      final parser = WsStreamParser();
      parser.addBytes(utf8.encode('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: x\r\n\r\n'));
      parser.setExpectedAcceptKey('x');
      parser.tryParseHandshake();

      parser.addBytes(frameBytes);
      final decoded = parser.tryParseFrame();
      expect(decoded, isNotNull);
      expect(decoded!.payload.length, 300);
      expect(utf8.decode(decoded.payload), text);
    });

    test('WsStreamParser handles unmasked server-to-client frame', () {
      final text = 'Server unmasked text';
      final payload = utf8.encode(text);
      final frameBytes = encodeWsFrame(payload, opcode: 0x1, fin: true, masked: false);

      final parser = WsStreamParser();
      parser.addBytes(utf8.encode('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: x\r\n\r\n'));
      parser.setExpectedAcceptKey('x');
      parser.tryParseHandshake();

      parser.addBytes(frameBytes);
      final decoded = parser.tryParseFrame();
      expect(decoded, isNotNull);
      expect(utf8.decode(decoded!.payload), text);
    });

    test('WsStreamParser handles byte stream split across multiple chunks', () {
      final text = 'Payload split across network chunks';
      final payload = utf8.encode(text);
      final frameBytes = encodeWsFrame(payload, opcode: 0x1, fin: true, masked: false);

      final parser = WsStreamParser();
      parser.addBytes(utf8.encode('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: x\r\n\r\n'));
      parser.setExpectedAcceptKey('x');
      parser.tryParseHandshake();

      // Feed frame 3 bytes at a time
      DecodedWsFrame? decoded;
      for (var i = 0; i < frameBytes.length; i += 3) {
        final end = (i + 3 < frameBytes.length) ? i + 3 : frameBytes.length;
        parser.addBytes(frameBytes.sublist(i, end));
        decoded ??= parser.tryParseFrame();
      }

      expect(decoded, isNotNull);
      expect(utf8.decode(decoded!.payload), text);
    });
  });

  group('TsnetWsChannel Integration over Mock WsSocket', () {
    test('connects, exchanges handshake, receives text frames, and handles ping/pong', () async {
      final mockSocket = MockWsSocket();
      final connectFuture = TsnetWsChannel.connect(
        socket: mockSocket,
        host: '100.64.1.2:5050',
        path: '/api/ws?token=token123',
      );

      // Verify handshake request sent
      expect(mockSocket.sentBytes.length, 1);
      final sentReq = utf8.decode(mockSocket.sentBytes[0]);
      expect(sentReq, contains('GET /api/ws?token=token123 HTTP/1.1'));

      // Extract Sec-WebSocket-Key from sent request
      final keyMatch = RegExp(r'Sec-WebSocket-Key: (.*)\r\n').firstMatch(sentReq);
      expect(keyMatch, isNotNull);
      final clientKey = keyMatch!.group(1)!;
      final acceptKey = computeSecWebSocketAccept(clientKey);

      // Push server handshake 101 response back
      final handshakeResponse =
          'HTTP/1.1 101 Switching Protocols\r\n'
          'Upgrade: websocket\r\n'
          'Connection: Upgrade\r\n'
          'Sec-WebSocket-Accept: $acceptKey\r\n\r\n';
      mockSocket.pushInput(Uint8List.fromList(utf8.encode(handshakeResponse)));

      final channel = await connectFuture;

      final messages = <String>[];
      final sub = channel.stream.listen((msg) => messages.add(msg));

      // Push text frame from server
      final msgFrame = encodeWsFrame(
        utf8.encode('{"DirectoryPath":"C:/task","State":{"Status":"Running"}}'),
        opcode: 0x1,
        fin: true,
        masked: false,
      );
      mockSocket.pushInput(msgFrame);

      await pumpEventQueue();
      expect(messages, hasLength(1));
      expect(messages[0], contains('"DirectoryPath":"C:/task"'));

      // Push Ping frame from server
      final pingFrame = encodeWsFrame(utf8.encode('ping-data'), opcode: 0x9, fin: true, masked: false);
      mockSocket.pushInput(pingFrame);

      await pumpEventQueue();
      // Verify Pong frame sent back over mockSocket
      expect(mockSocket.sentBytes.length, 2);
      final pongBytes = mockSocket.sentBytes[1];
      expect(pongBytes[0] & 0x0F, 0x0A); // Opcode 0xA = Pong

      await channel.close();
      await sub.cancel();
      expect(mockSocket.isClosed, isTrue);
    });
  });
}
