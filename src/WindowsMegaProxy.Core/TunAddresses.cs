using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace WindowsMegaProxy;

public static class TunAddresses
{
    public static bool HasIpv6Uplink(IEnumerable<IPAddress> addresses, IEnumerable<IPAddress> gateways) =>
        addresses.Any(address => address.AddressFamily == AddressFamily.InterNetworkV6 &&
            !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !IPAddress.IsLoopback(address) &&
            !address.Equals(IPAddress.IPv6Any) && (address.GetAddressBytes()[0] & 0xfe) != 0xfc) &&
        gateways.Any(address => address.AddressFamily == AddressFamily.InterNetworkV6 &&
            !address.Equals(IPAddress.IPv6Any));

    public static void Apply(JsonObject config, (string Ipv4, string Ipv6) addresses, bool ipv6)
    {
        config["inbounds"]![0]!["address"] = ipv6 ? new JsonArray(addresses.Ipv4, addresses.Ipv6) : new JsonArray(addresses.Ipv4);
        if (ipv6) return;
        config["dns"]!["strategy"] = "ipv4_only";
        config["route"]!["rules"]!.AsArray().Insert(0, new JsonObject
        {
            ["ip_version"] = 6, ["action"] = "reject"
        });
    }

    public static IPNetwork Network(IPAddress address, int prefixLength)
    {
        byte[] bytes = address.GetAddressBytes();
        for (int i = 0; i < bytes.Length; i++) bytes[i] &= (byte)(255 << (8 - Math.Clamp(prefixLength - i * 8, 0, 8)));
        return new IPNetwork(new IPAddress(bytes), prefixLength);
    }

    public static (string Ipv4, string Ipv6) Select(IEnumerable<IPNetwork> occupied, bool dev)
    {
        var networks = occupied.ToArray();
        string Find(bool ipv6)
        {
            string[] pools = ipv6 ? ["fd87:6d65:6761", "fd87:6d65:6762"] : ["172.29", "172.30", "172.31", "10.255", "192.168"];
            foreach (string pool in pools)
                for (int index = 0; index < 256; index++)
                {
                    int slot = (index + (dev ? 128 : 0)) % 256;
                    var candidate = IPNetwork.Parse(ipv6 ? $"{pool}:{slot:x}::/126" : $"{pool}.{slot}.0/30");
                    if (networks.Any(network => network.BaseAddress.AddressFamily == candidate.BaseAddress.AddressFamily &&
                        (network.Contains(candidate.BaseAddress) || candidate.Contains(network.BaseAddress)))) continue;
                    return ipv6 ? $"{pool}:{slot:x}::1/126" : $"{pool}.{slot}.1/30";
                }
            throw new IOException(ipv6 ? "No unused private IPv6 subnet is available for TUN." : "No unused private IPv4 subnet is available for TUN.");
        }
        return (Find(false), Find(true));
    }
}
