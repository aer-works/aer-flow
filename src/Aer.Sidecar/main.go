// Command aer-sidecar embeds a Tailscale node (via tsnet) so Aer.Daemon's --remote mode needs
// no separately-installed Tailscale app (M21 Phase 5, #242). It never touches HTTP itself: it
// accepts connections on its own tsnet interface and splices each one, byte for byte, to
// Aer.Daemon's Kestrel listener on loopback — Kestrel stays loopback-only forever (Phase 6, #243),
// and this is the only thing that ever reaches an address other machines can see.
//
// tsnet runs a userspace (gVisor) network stack that exists only inside this process — there is
// no OS-level network interface for a sibling process to bind to, which is why Kestrel cannot bind
// the tailnet address directly (see docs/decisions-of-record.md, M21: zero-config Tailscale TCP splicing).
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"regexp"
	"sync"

	"tailscale.com/tsnet"
)

// status is the sidecar's own loopback-only status endpoint's payload — polled by Aer.Daemon
// (spawn-and-poll, the same shape TaskSession.SpawnDaemonProcessAsync already uses to wait for
// Aer.Daemon itself to come up) instead of scraping tsnet's log output for the auth URL.
type status struct {
	Ready       bool   `json:"ready"`
	AuthURL     string `json:"authUrl,omitempty"`
	TailscaleIP string `json:"tailscaleIp,omitempty"`
	Error       string `json:"error,omitempty"`
}

type statusHolder struct {
	mu sync.RWMutex
	s  status
}

func (h *statusHolder) set(mutate func(*status)) {
	h.mu.Lock()
	defer h.mu.Unlock()
	mutate(&h.s)
}

func (h *statusHolder) get() status {
	h.mu.RLock()
	defer h.mu.RUnlock()
	return h.s
}

// authURLPattern matches the interactive-login line tsnet's UserLogf emits on first enrollment
// (e.g. "To authenticate, visit: https://login.tailscale.com/a/0123456789abcdef"). There is no
// first-class getter for this on tsnet.Server, so this is the documented approach: watch the log.
var authURLPattern = regexp.MustCompile(`(https://login\.tailscale\.com/\S+)`)

// forgetAndReauth signs the sidecar out of its current tailnet and immediately calls srv.Up again
// to re-enter the interactive-login flow -- the same call main() itself blocks on during first-run
// enrollment, so UserLogf emits a fresh authURLPattern match the same way, and Aer.Daemon's existing
// sidecar-status poll picks up the new AuthUrl without any additional wiring.
func forgetAndReauth(srv *tsnet.Server, holder *statusHolder) {
	ctx := context.Background()

	lc, err := srv.LocalClient()
	if err != nil {
		holder.set(func(s *status) { s.Error = fmt.Sprintf("could not get local client: %v", err) })
		return
	}

	if err := lc.Logout(ctx); err != nil {
		holder.set(func(s *status) { s.Error = fmt.Sprintf("logout failed: %v", err) })
		return
	}

	holder.set(func(s *status) {
		s.Ready = false
		s.AuthURL = ""
		s.TailscaleIP = ""
		s.Error = ""
	})

	tsStatus, err := srv.Up(ctx)
	if err != nil {
		holder.set(func(s *status) { s.Error = err.Error() })
		return
	}

	var tailscaleIP string
	if len(tsStatus.TailscaleIPs) > 0 {
		tailscaleIP = tsStatus.TailscaleIPs[0].String()
	}
	holder.set(func(s *status) {
		s.Ready = true
		s.AuthURL = ""
		s.TailscaleIP = tailscaleIP
	})
}

func main() {
	kestrelPort := flag.Int("kestrel-port", 0, "Aer.Daemon's loopback Kestrel port to splice traffic to (required)")
	statusPort := flag.Int("status-port", 0, "loopback port for this sidecar's own status endpoint (0 = OS-assigned)")
	statusPortFile := flag.String("status-port-file", "", "file to write the assigned status port to, once known (required)")
	stateDir := flag.String("state-dir", "", "tsnet state directory (required)")
	hostname := flag.String("hostname", "aer-desktop", "tailnet hostname to advertise")
	flag.Parse()

	if *kestrelPort == 0 || *stateDir == "" || *statusPortFile == "" {
		fmt.Fprintln(os.Stderr, "aer-sidecar: --kestrel-port, --state-dir, and --status-port-file are required")
		os.Exit(2)
	}

	holder := &statusHolder{}

	statusListener, err := net.Listen("tcp", fmt.Sprintf("127.0.0.1:%d", *statusPort))
	if err != nil {
		log.Fatalf("aer-sidecar: could not bind status listener: %v", err)
	}
	assignedPort := statusListener.Addr().(*net.TCPAddr).Port
	if err := os.WriteFile(*statusPortFile, []byte(fmt.Sprintf("%d", assignedPort)), 0o600); err != nil {
		log.Fatalf("aer-sidecar: could not write status port file: %v", err)
	}

	srv := &tsnet.Server{
		Dir:      *stateDir,
		Hostname: *hostname,
		UserLogf: func(format string, args ...any) {
			line := fmt.Sprintf(format, args...)
			log.Print(line)
			if m := authURLPattern.FindStringSubmatch(line); m != nil {
				holder.set(func(s *status) { s.AuthURL = m[1] })
			}
		},
	}
	defer srv.Close()

	mux := http.NewServeMux()
	mux.HandleFunc("/status", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(holder.get())
	})
	// /forget (M21 Phase 5 follow-up): the in-app counterpart of deleting the node from the
	// Tailscale admin console and restarting Aer.Daemon, which was previously the only way to
	// disconnect it. Runs in the background -- forgetAndReauth re-enters the same interactive-login
	// flow main() itself blocks on below, so the fresh AuthUrl surfaces via UserLogf/holder exactly
	// like first-run enrollment does.
	mux.HandleFunc("/forget", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			w.WriteHeader(http.StatusMethodNotAllowed)
			return
		}
		if !holder.get().Ready {
			w.WriteHeader(http.StatusConflict)
			return
		}
		go forgetAndReauth(srv, holder)
		w.WriteHeader(http.StatusAccepted)
	})
	go func() {
		_ = http.Serve(statusListener, mux)
	}()

	ctx := context.Background()
	tsStatus, err := srv.Up(ctx)
	if err != nil {
		holder.set(func(s *status) { s.Error = err.Error() })
		log.Fatalf("aer-sidecar: tsnet Up failed: %v", err)
	}

	var tailscaleIP string
	if len(tsStatus.TailscaleIPs) > 0 {
		tailscaleIP = tsStatus.TailscaleIPs[0].String()
	}
	holder.set(func(s *status) {
		s.Ready = true
		s.AuthURL = ""
		s.TailscaleIP = tailscaleIP
	})

	ln, err := srv.Listen("tcp", fmt.Sprintf(":%d", *kestrelPort))
	if err != nil {
		holder.set(func(s *status) { s.Error = err.Error() })
		log.Fatalf("aer-sidecar: tsnet Listen failed: %v", err)
	}
	defer ln.Close()

	log.Printf("aer-sidecar: ready, tailnet IP %s, splicing to 127.0.0.1:%d", tailscaleIP, *kestrelPort)

	kestrelAddr := fmt.Sprintf("127.0.0.1:%d", *kestrelPort)
	for {
		conn, err := ln.Accept()
		if err != nil {
			log.Printf("aer-sidecar: accept failed: %v", err)
			return
		}
		go handleConn(conn, kestrelAddr)
	}
}

// handleConn splices conn (a connection accepted on the tsnet interface) to Kestrel's loopback
// port. This is deliberately protocol-agnostic — a raw byte copy in both directions — so HTTP and
// WebSocket upgrades pass through with zero HTTP awareness in this process.
func handleConn(remoteConn net.Conn, kestrelAddr string) {
	defer remoteConn.Close()

	localConn, err := net.Dial("tcp", kestrelAddr)
	if err != nil {
		log.Printf("aer-sidecar: could not dial Kestrel at %s: %v", kestrelAddr, err)
		return
	}
	defer localConn.Close()

	var wg sync.WaitGroup
	wg.Add(2)
	go func() {
		defer wg.Done()
		_, _ = io.Copy(localConn, remoteConn)
		if tc, ok := localConn.(*net.TCPConn); ok {
			_ = tc.CloseWrite()
		}
	}()
	go func() {
		defer wg.Done()
		_, _ = io.Copy(remoteConn, localConn)
		if tc, ok := remoteConn.(*net.TCPConn); ok {
			_ = tc.CloseWrite()
		}
	}()
	wg.Wait()
}
