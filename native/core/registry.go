package main

import (
	"context"

	box "github.com/sagernet/sing-box"
	"github.com/sagernet/sing-box/adapter/certificate"
	"github.com/sagernet/sing-box/adapter/endpoint"
	"github.com/sagernet/sing-box/adapter/inbound"
	"github.com/sagernet/sing-box/adapter/outbound"
	"github.com/sagernet/sing-box/adapter/service"
	"github.com/sagernet/sing-box/dns"
	"github.com/sagernet/sing-box/dns/transport"
	"github.com/sagernet/sing-box/dns/transport/hosts"
	"github.com/sagernet/sing-box/dns/transport/local"
	_ "github.com/sagernet/sing-box/experimental/clashapi"
	"github.com/sagernet/sing-box/protocol/direct"
	"github.com/sagernet/sing-box/protocol/http"
	"github.com/sagernet/sing-box/protocol/mixed"
	"github.com/sagernet/sing-box/protocol/socks"
	"github.com/sagernet/sing-box/protocol/ssh"
	"github.com/sagernet/sing-box/protocol/tun"
)

func coreContext(ctx context.Context) context.Context {
	inbounds := inbound.NewRegistry()
	tun.RegisterInbound(inbounds)
	socks.RegisterInbound(inbounds)
	mixed.RegisterInbound(inbounds)
	outbounds := outbound.NewRegistry()
	direct.RegisterOutbound(outbounds)
	http.RegisterOutbound(outbounds)
	socks.RegisterOutbound(outbounds)
	ssh.RegisterOutbound(outbounds)
	servers := dns.NewTransportRegistry()
	transport.RegisterTCP(servers)
	transport.RegisterUDP(servers)
	transport.RegisterTLS(servers)
	transport.RegisterHTTPS(servers)
	local.RegisterTransport(servers)
	hosts.RegisterTransport(servers)
	return box.Context(ctx, inbounds, outbounds, endpoint.NewRegistry(), servers,
		service.NewRegistry(), certificate.NewRegistry())
}
