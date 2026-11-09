package main

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"sort"
	"strings"
	"time"

	"github.com/sagernet/quic-go"
	"github.com/sagernet/quic-go/http3"
	M "github.com/sagernet/sing/common/metadata"
	"github.com/sagernet/sing/protocol/socks"
)

type options struct {
	Port      int
	Secret    string
	Megabytes int
}
type result struct {
	Ip       string
	Country  string
	Protocol string
	Latency  string
	Download string
	Upload   string
	Status   string
}
type event struct {
	Result   *result `json:",omitempty"`
	Error    string  `json:",omitempty"`
	Fallback bool    `json:",omitempty"`
}
type dialer struct{}

func (dialer) DialContext(ctx context.Context, network string, address M.Socksaddr) (net.Conn, error) {
	return (&net.Dialer{}).DialContext(ctx, network, address.String())
}
func (dialer) ListenPacket(ctx context.Context, address M.Socksaddr) (net.PacketConn, error) {
	return (&net.ListenConfig{}).ListenPacket(ctx, "udp", "")
}

func transport(o options) *http3.Transport {
	t := &http3.Transport{}
	if o.Port != 0 {
		relay := socks.NewClient(dialer{}, M.ParseSocksaddr(fmt.Sprintf("127.0.0.1:%d", o.Port)), socks.Version5, "diagnostic", o.Secret)
		t.Dial = func(ctx context.Context, address string, tlsConfig *tls.Config, config *quic.Config) (*quic.Conn, error) {
			udp, err := relay.DialContext(ctx, "udp", M.ParseSocksaddr(address))
			if err != nil {
				return nil, err
			}
			conn, err := quic.DialEarlyConn(ctx, udp, tlsConfig, config)
			if err != nil {
				udp.Close()
				return nil, err
			}
			go func() { <-conn.Context().Done(); udp.Close() }()
			return conn, nil
		}
	}
	return t
}

type countedZeros struct{ sent int64 }

func (z *countedZeros) Read(p []byte) (int, error) {
	clear(p)
	z.sent += int64(len(p))
	return len(p), nil
}

func transfer(ctx context.Context, client *http.Client, method, url string, size int64) (time.Duration, error) {
	var body io.Reader

	upload := &countedZeros{}
	if method == http.MethodPost {
		body = io.LimitReader(upload, size)
	}
	req, err := http.NewRequestWithContext(ctx, method, url, body)
	if err != nil {
		return 0, err
	}
	if body != nil {
		req.ContentLength = size
	}
	started := time.Now()
	response, err := client.Do(req)
	if err != nil {
		return 0, err
	}
	defer response.Body.Close()
	if response.StatusCode != 200 || response.ProtoMajor != 3 {
		return 0, errors.New("unexpected HTTP response")
	}
	headers := time.Since(started)
	limit := size
	if method == http.MethodPost {
		limit = 65536
	}
	received, err := io.Copy(io.Discard, io.LimitReader(response.Body, limit+1))
	if err != nil {
		return 0, err
	}
	if received > limit || method == http.MethodGet && received != size || method == http.MethodPost && upload.sent != size {
		return 0, errors.New("incomplete transfer")
	}
	if size == 0 {
		return headers, nil
	}
	return time.Since(started), nil
}

func measure(ctx context.Context, o options, emit func(event)) error {
	t := transport(o)
	defer t.Close()
	client := &http.Client{Transport: t, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	const origin = "https://h3.speed.cloudflare.com"
	initial, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	req, _ := http.NewRequestWithContext(initial, http.MethodGet, origin+"/cdn-cgi/trace", nil)
	response, err := client.Do(req)
	if err != nil {
		var cert *tls.CertificateVerificationError
		var unknown x509.UnknownAuthorityError
		var hostname x509.HostnameError
		// Only metadata negotiation may fall back; verification and payload failures are terminal.
		if ctx.Err() == nil && !errors.As(err, &cert) && !errors.As(err, &unknown) && !errors.As(err, &hostname) {
			emit(event{Fallback: true})
			return nil
		}
		return errors.New("HTTP/3 negotiation failed")
	}
	defer response.Body.Close()
	if response.StatusCode != 200 || response.ProtoMajor != 3 {
		return errors.New("HTTP/3 metadata request failed")
	}
	data, err := io.ReadAll(io.LimitReader(response.Body, 16385))
	if err != nil || len(data) > 16384 {
		return errors.New("invalid speed metadata")
	}
	r := result{Status: "Running", Protocol: "HTTP/3", Latency: "—", Download: "—", Upload: "—"}
	for _, line := range strings.Split(string(data), "\n") {
		key, value, _ := strings.Cut(strings.TrimSpace(line), "=")
		if key == "ip" && net.ParseIP(value) != nil {
			r.Ip = value
		}
		if key == "loc" && len(value) == 2 && value[0] >= 'A' && value[0] <= 'Z' && value[1] >= 'A' && value[1] <= 'Z' {
			r.Country = value
		}
	}
	if r.Ip == "" {
		return errors.New("invalid speed metadata")
	}
	if response.TLS != nil {
		r.Protocol += " · " + tls.VersionName(response.TLS.Version)
	}
	emit(event{Result: &r})
	latency := make([]time.Duration, 3)
	for i := range latency {
		latency[i], err = transfer(ctx, client, http.MethodGet, origin+"/__down?bytes=0", 0)
		if err != nil {
			return err
		}
	}
	sort.Slice(latency, func(i, j int) bool { return latency[i] < latency[j] })
	r.Latency = fmt.Sprintf("%d ms", latency[1].Milliseconds())
	emit(event{Result: &r})
	size := int64(o.Megabytes) * 1000000
	duration, err := transfer(ctx, client, http.MethodGet, fmt.Sprintf("%s/__down?bytes=%d", origin, size), size)
	if err != nil {
		return err
	}
	r.Download = fmt.Sprintf("%.2f Mbps", float64(size)*8/duration.Seconds()/1000000)
	emit(event{Result: &r})
	duration, err = transfer(ctx, client, http.MethodPost, origin+"/__up", size)
	if err != nil {
		return err
	}
	r.Upload = fmt.Sprintf("%.2f Mbps", float64(size)*8/duration.Seconds()/1000000)
	r.Status = "Done"
	emit(event{Result: &r})
	return nil
}

func main() {
	encoder := json.NewEncoder(os.Stdout)
	emit := func(e event) { _ = encoder.Encode(e) }
	var o options
	if err := json.NewDecoder(io.LimitReader(os.Stdin, 4096)).Decode(&o); err != nil ||
		o.Megabytes != 10 && o.Megabytes != 25 && o.Megabytes != 50 || o.Port < 0 || o.Port > 65535 ||
		o.Port != 0 && (len(o.Secret) == 0 || len(o.Secret) > 255) {
		emit(event{Error: "Invalid diagnostic options"})
		os.Exit(1)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()
	if err := measure(ctx, o, emit); err != nil {
		emit(event{Error: "HTTP/3 speed test failed"})
		os.Exit(1)
	}
}
