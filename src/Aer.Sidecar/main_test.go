package main

import (
	"io"
	"net"
	"testing"
	"time"
)

// TestHandleConn_SplicesBothDirections proves handleConn is a transparent byte-for-byte proxy in
// both directions — the actual property Kestrel's loopback-only rebind (Phase 6, #243) depends on:
// an HTTP request/response (or WebSocket upgrade) crossing the tsnet interface must reach Kestrel
// and come back unmodified, with no HTTP awareness in this process at all.
func TestHandleConn_SplicesBothDirections(t *testing.T) {
	// Fake Kestrel: echoes whatever it reads, prefixed, so we can tell request bytes traveled in
	// and response bytes traveled back out through handleConn's two io.Copy directions.
	kestrelListener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	defer kestrelListener.Close()

	go func() {
		conn, err := kestrelListener.Accept()
		if err != nil {
			return
		}
		defer conn.Close()

		buf := make([]byte, 1024)
		n, err := conn.Read(buf)
		if err != nil {
			return
		}
		_, _ = conn.Write(append([]byte("echo:"), buf[:n]...))
	}()

	// Fake "remote" side: what would normally be the tsnet-accepted connection.
	remoteListener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	defer remoteListener.Close()

	acceptedCh := make(chan net.Conn, 1)
	go func() {
		conn, err := remoteListener.Accept()
		if err == nil {
			acceptedCh <- conn
		}
	}()

	clientConn, err := net.Dial("tcp", remoteListener.Addr().String())
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer clientConn.Close()

	acceptedConn := <-acceptedCh
	go handleConn(acceptedConn, kestrelListener.Addr().String())

	if _, err := clientConn.Write([]byte("hello")); err != nil {
		t.Fatalf("write: %v", err)
	}

	_ = clientConn.SetReadDeadline(time.Now().Add(5 * time.Second))
	response := make([]byte, 1024)
	n, err := io.ReadFull(clientConn, response[:10])
	if err != nil {
		t.Fatalf("read response: %v", err)
	}

	want := "echo:hello"
	got := string(response[:n])
	if got != want {
		t.Fatalf("got %q, want %q", got, want)
	}
}
