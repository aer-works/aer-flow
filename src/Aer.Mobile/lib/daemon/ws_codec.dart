import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

/// SHA-1 digest calculation for RFC 6455 Sec-WebSocket-Accept validation.
Uint8List sha1Hash(Uint8List bytes) {
  var h0 = 0x67452301;
  var h1 = 0xEFCDAB89;
  var h2 = 0x98BADCFE;
  var h3 = 0x10325476;
  var h4 = 0xC3D2E1F0;

  final bitLen = bytes.length * 8;
  final padLen = (56 - (bytes.length + 1) % 64) % 64;
  final padded = Uint8List(bytes.length + 1 + padLen + 8);
  padded.setAll(0, bytes);
  padded[bytes.length] = 0x80;

  final bd = ByteData.sublistView(padded);
  bd.setUint64(padded.length - 8, bitLen, Endian.big);

  int rotl32(int val, int shift) {
    val &= 0xFFFFFFFF;
    return ((val << shift) | (val >>> (32 - shift))) & 0xFFFFFFFF;
  }

  final w = Int32List(80);
  for (var i = 0; i < padded.length; i += 64) {
    for (var t = 0; t < 16; t++) {
      w[t] = bd.getUint32(i + t * 4, Endian.big);
    }
    for (var t = 16; t < 80; t++) {
      w[t] = rotl32(w[t - 3] ^ w[t - 8] ^ w[t - 14] ^ w[t - 16], 1);
    }

    var a = h0;
    var b = h1;
    var c = h2;
    var d = h3;
    var e = h4;

    for (var t = 0; t < 80; t++) {
      int f, k;
      if (t < 20) {
        f = (b & c) | ((~b) & d);
        k = 0x5A827999;
      } else if (t < 40) {
        f = b ^ c ^ d;
        k = 0x6ED9EBA1;
      } else if (t < 60) {
        f = (b & c) | (b & d) | (c & d);
        k = 0x8F1BBCDC;
      } else {
        f = b ^ c ^ d;
        k = 0xCA62C1D6;
      }

      final temp = (rotl32(a, 5) + f + e + k + w[t]) & 0xFFFFFFFF;
      e = d;
      d = c;
      c = rotl32(b, 30);
      b = a;
      a = temp;
    }

    h0 = (h0 + a) & 0xFFFFFFFF;
    h1 = (h1 + b) & 0xFFFFFFFF;
    h2 = (h2 + c) & 0xFFFFFFFF;
    h3 = (h3 + d) & 0xFFFFFFFF;
    h4 = (h4 + e) & 0xFFFFFFFF;
  }

  final result = Uint8List(20);
  final rbd = ByteData.sublistView(result);
  rbd.setUint32(0, h0, Endian.big);
  rbd.setUint32(4, h1, Endian.big);
  rbd.setUint32(8, h2, Endian.big);
  rbd.setUint32(12, h3, Endian.big);
  rbd.setUint32(16, h4, Endian.big);
  return result;
}

String generateSecWebSocketKey([Random? random]) {
  final r = random ?? Random.secure();
  final bytes = Uint8List.fromList(List<int>.generate(16, (_) => r.nextInt(256)));
  return base64.encode(bytes);
}

String computeSecWebSocketAccept(String key) {
  const magicGuid = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';
  final concatenated = '$key$magicGuid';
  final hash = sha1Hash(Uint8List.fromList(utf8.encode(concatenated)));
  return base64.encode(hash);
}

String buildHandshakeRequest({
  required String host,
  required String path,
  required String secWebSocketKey,
}) {
  return 'GET $path HTTP/1.1\r\n'
      'Host: $host\r\n'
      'Upgrade: websocket\r\n'
      'Connection: Upgrade\r\n'
      'Sec-WebSocket-Key: $secWebSocketKey\r\n'
      'Sec-WebSocket-Version: 13\r\n\r\n';
}

void verifyHandshakeResponse(String responseHeaders, String expectedAcceptKey) {
  final lines = responseHeaders.split('\r\n');
  if (lines.isEmpty) {
    throw const FormatException('Empty handshake response');
  }
  final statusLine = lines[0];
  final statusParts = statusLine.split(' ');
  if (statusParts.length < 2 || statusParts[1] != '101') {
    throw FormatException('WebSocket handshake failed: $statusLine');
  }

  final headers = <String, String>{};
  for (var i = 1; i < lines.length; i++) {
    final line = lines[i];
    final colonIdx = line.indexOf(':');
    if (colonIdx != -1) {
      final k = line.substring(0, colonIdx).trim().toLowerCase();
      final v = line.substring(colonIdx + 1).trim();
      headers[k] = v;
    }
  }

  if (headers['upgrade']?.toLowerCase() != 'websocket') {
    throw const FormatException('Missing or invalid Upgrade header in handshake response');
  }
  final connectionHeader = headers['connection']?.toLowerCase() ?? '';
  if (!connectionHeader.contains('upgrade')) {
    throw const FormatException('Missing or invalid Connection header in handshake response');
  }
  final accept = headers['sec-websocket-accept'];
  if (accept != expectedAcceptKey) {
    throw FormatException(
      'Sec-WebSocket-Accept mismatch (got $accept, expected $expectedAcceptKey)',
    );
  }
}

/// Encodes an RFC 6455 WebSocket frame (Client -> Server MUST be masked by default).
Uint8List encodeWsFrame(
  List<int> payload, {
  int opcode = 0x1,
  bool fin = true,
  bool masked = true,
  List<int>? maskKey,
  Random? random,
}) {
  final r = random ?? Random.secure();
  final mask = masked
      ? (maskKey != null && maskKey.length == 4
          ? Uint8List.fromList(maskKey)
          : Uint8List.fromList(List<int>.generate(4, (_) => r.nextInt(256))))
      : null;

  final b0 = (fin ? 0x80 : 0x00) | (opcode & 0x0F);
  final len = payload.length;

  int headerLen;
  if (len < 126) {
    headerLen = 2;
  } else if (len <= 65535) {
    headerLen = 4;
  } else {
    headerLen = 10;
  }
  if (masked) {
    headerLen += 4;
  }

  final frame = Uint8List(headerLen + len);
  frame[0] = b0;

  var pos = 1;
  if (len < 126) {
    frame[pos++] = (masked ? 0x80 : 0x00) | len;
  } else if (len <= 65535) {
    frame[pos++] = (masked ? 0x80 : 0x00) | 126;
    frame[pos++] = (len >> 8) & 0xFF;
    frame[pos++] = len & 0xFF;
  } else {
    frame[pos++] = (masked ? 0x80 : 0x00) | 127;
    final bd = ByteData.sublistView(frame, pos, pos + 8);
    bd.setUint64(0, len, Endian.big);
    pos += 8;
  }

  if (masked && mask != null) {
    frame.setAll(pos, mask);
    pos += 4;
    for (var i = 0; i < len; i++) {
      frame[pos + i] = payload[i] ^ mask[i % 4];
    }
  } else {
    frame.setAll(pos, payload);
  }

  return frame;
}

class DecodedWsFrame {
  final bool fin;
  final int opcode;
  final Uint8List payload;

  DecodedWsFrame({
    required this.fin,
    required this.opcode,
    required this.payload,
  });
}

/// Incremental parser for RFC 6455 stream data (handshake + incoming frames).
class WsStreamParser {
  final BytesBuilder _buffer = BytesBuilder(copy: false);
  bool _handshakeComplete = false;
  String? _expectedAcceptKey;

  bool get isHandshakeComplete => _handshakeComplete;

  void setExpectedAcceptKey(String acceptKey) {
    _expectedAcceptKey = acceptKey;
  }

  void addBytes(Uint8List bytes) {
    _buffer.add(bytes);
  }

  bool tryParseHandshake() {
    if (_handshakeComplete) return false;

    final bytes = _buffer.toBytes();
    for (var i = 0; i <= bytes.length - 4; i++) {
      if (bytes[i] == 13 &&
          bytes[i + 1] == 10 &&
          bytes[i + 2] == 13 &&
          bytes[i + 3] == 10) {
        final headerText = utf8.decode(bytes.sublist(0, i));
        verifyHandshakeResponse(headerText, _expectedAcceptKey ?? '');
        _handshakeComplete = true;

        final remaining = bytes.sublist(i + 4);
        _buffer.clear();
        if (remaining.isNotEmpty) {
          _buffer.add(remaining);
        }
        return true;
      }
    }
    return false;
  }

  DecodedWsFrame? tryParseFrame() {
    if (!_handshakeComplete) return null;

    final bytes = _buffer.toBytes();
    if (bytes.length < 2) return null;

    final b0 = bytes[0];
    final fin = (b0 & 0x80) != 0;
    final opcode = b0 & 0x0F;

    final b1 = bytes[1];
    final masked = (b1 & 0x80) != 0;
    final len7 = b1 & 0x7F;

    var headerLen = 2;
    int payloadLen;

    if (len7 < 126) {
      payloadLen = len7;
    } else if (len7 == 126) {
      if (bytes.length < 4) return null;
      payloadLen = (bytes[2] << 8) | bytes[3];
      headerLen = 4;
    } else {
      if (bytes.length < 10) return null;
      final bd = ByteData.sublistView(bytes, 2, 10);
      payloadLen = bd.getUint64(0, Endian.big);
      headerLen = 10;
    }

    final maskOffset = headerLen;
    if (masked) {
      headerLen += 4;
    }

    final totalLen = headerLen + payloadLen;
    if (bytes.length < totalLen) return null;

    final rawPayload = bytes.sublist(headerLen, totalLen);
    final payload = Uint8List(payloadLen);

    if (masked) {
      final maskKey = bytes.sublist(maskOffset, maskOffset + 4);
      for (var i = 0; i < payloadLen; i++) {
        payload[i] = rawPayload[i] ^ maskKey[i % 4];
      }
    } else {
      payload.setAll(0, rawPayload);
    }

    final remaining = bytes.sublist(totalLen);
    _buffer.clear();
    if (remaining.isNotEmpty) {
      _buffer.add(remaining);
    }

    return DecodedWsFrame(fin: fin, opcode: opcode, payload: payload);
  }
}
