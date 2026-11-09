package main

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
	"time"

	"github.com/sagernet/quic-go/http3"
)

type zeros struct{}

func (zeros) Read(p []byte) (int, error) { clear(p); return len(p), nil }

func TestHttp3TransfersThroughSocks(t *testing.T) {
	certificate := httptest.NewTLSServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {}))
	defer certificate.Close()
	roots := x509.NewCertPool()
	roots.AddCert(certificate.Certificate())
	packet, err := net.ListenPacket("udp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	server := &http3.Server{TLSConfig: &tls.Config{Certificates: certificate.TLS.Certificates}, Handler: http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.ProtoMajor != 3 {
			t.Error("expected HTTP/3")
		}
		switch r.URL.Path {
		case "/down":
			_, _ = io.CopyN(w, zeros{}, 32000)
		case "/up":
			n, e := io.Copy(io.Discard, r.Body)
			if e != nil || n != 32000 {
				t.Errorf("upload: %d, %v", n, e)
			}
		case "/short":
			_, _ = w.Write([]byte("short"))
		default:
			w.WriteHeader(500)
		}
	})}
	go func() { _ = server.Serve(packet) }()
	defer server.Close()
	origin := "https://" + packet.LocalAddr().String()
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	port := listener.Addr().(*net.TCPAddr).Port
	listener.Close()
	config := filepath.Join(t.TempDir(), "core.json")
	data := fmt.Sprintf(`{"log":{"disabled":true},"inbounds":[{"type":"socks","listen":"127.0.0.1","listen_port":%d,"users":[{"username":"diagnostic","password":"test"}]}],"outbounds":[{"type":"direct"}]}`, port)
	if err := os.WriteFile(config, []byte(data), 0600); err != nil {
		t.Fatal(err)
	}
	core, err := filepath.Abs("../../.tools/sing-box/sing-box.exe")
	if err != nil {
		t.Fatal(err)
	}
	process := exec.Command(core, "run", "-c", config)
	if err := process.Start(); err != nil {
		t.Fatal(err)
	}
	defer func() { _ = process.Process.Kill(); _ = process.Wait() }()
	deadline := time.Now().Add(10 * time.Second)
	for {
		conn, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", port), 100*time.Millisecond)
		if err == nil {
			conn.Close()
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("SOCKS core did not start")
		}
		time.Sleep(25 * time.Millisecond)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	for _, proxy := range []int{0, port} {
		transport := transport(options{Port: proxy, Secret: "test"})
		transport.TLSClientConfig = &tls.Config{RootCAs: roots}
		client := &http.Client{Transport: transport}
		for _, method := range []string{http.MethodGet, http.MethodPost} {
			path := "/down"
			if method == http.MethodPost {
				path = "/up"
			}
			if _, err := transfer(ctx, client, method, origin+path, 32000); err != nil {
				t.Fatalf("proxy=%d method=%s: %v", proxy, method, err)
			}
		}
		if _, err := transfer(ctx, client, http.MethodGet, origin+"/short", 32000); err == nil {
			t.Fatal("truncated download accepted")
		}
		if _, err := transfer(ctx, client, http.MethodGet, origin+"/error", 32000); err == nil {
			t.Fatal("HTTP failure accepted")
		}
		transport.Close()
	}
	untrusted := transport(options{Port: port, Secret: "test"})
	defer untrusted.Close()
	if _, err := transfer(ctx, &http.Client{Transport: untrusted}, http.MethodGet, origin+"/down", 32000); err == nil {
		t.Fatal("untrusted TLS certificate accepted")
	}
	rejected := transport(options{Port: port, Secret: "wrong"})
	defer rejected.Close()
	rejected.TLSClientConfig = &tls.Config{RootCAs: roots}
	if _, err := transfer(ctx, &http.Client{Transport: rejected}, http.MethodGet, origin+"/down", 32000); err == nil {
		t.Fatal("SOCKS authentication failure bypassed")
	}
}
