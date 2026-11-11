package main

/*
#include <stdlib.h>
*/
import "C"

import (
	"context"
	"crypto/tls"
	"errors"
	"io"
	"net"
	"net/http"
	"net/url"
	"time"

	"github.com/sagernet/quic-go"
	"github.com/sagernet/quic-go/http3"
	M "github.com/sagernet/sing/common/metadata"
)

//export MegaProxyFetch
func MegaProxyFetch(config, address *C.char) *C.char {
	return execute(func() error {
		if config == nil || address == nil {
			return errors.New("Configuration and URL are required.")
		}
		target, err := url.Parse(C.GoString(address))
		if err != nil {
			return err
		}
		if target.Scheme != "https" && target.Scheme != "http" && target.Scheme != "http3" {
			return errors.New("Unsupported probe URL scheme.")
		}
		instance, cancel, err := create(C.GoString(config))
		if err != nil {
			return err
		}
		defer cancel()
		defer instance.Close()
		if err = instance.PreStart(); err != nil {
			return err
		}
		dialer := instance.Outbound().Default()
		client := &http.Client{Timeout: 12 * time.Second}
		if target.Scheme == "http3" {
			target.Scheme = "https"
			transport := &http3.Transport{Dial: func(ctx context.Context, address string, tlsConfig *tls.Config, config *quic.Config) (*quic.Conn, error) {
				udp, err := dialer.DialContext(ctx, "udp", M.ParseSocksaddr(address))
				if err != nil {
					return nil, err
				}
				connection, err := quic.DialEarlyConn(ctx, udp, tlsConfig, config)
				if err != nil {
					udp.Close()
					return nil, err
				}
				go func() { <-connection.Context().Done(); udp.Close() }()
				return connection, nil
			}}
			defer transport.Close()
			client.Transport = transport
		} else {
			transport := &http.Transport{ForceAttemptHTTP2: true, DialContext: func(ctx context.Context, network, address string) (net.Conn, error) {
				return dialer.DialContext(ctx, network, M.ParseSocksaddr(address))
			}}
			defer transport.CloseIdleConnections()
			client.Transport = transport
		}
		response, err := client.Get(target.String())
		if err != nil {
			return err
		}
		defer response.Body.Close()
		_, err = io.Copy(io.Discard, io.LimitReader(response.Body, 1024*1024))
		return err
	})
}
