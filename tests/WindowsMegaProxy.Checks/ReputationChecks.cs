using System;
using WindowsMegaProxy;

static class ReputationChecks
{
    public static void Run()
    {
        void Check(int status, string body, string expected)
        {
            if (IpReputation.Parse(status, body, "192.0.2.1") != expected) throw new Exception("Incorrect IP reputation verdict.");
        }
        Check(200, """{"status":"ok","192.0.2.1":{"detections":{"vpn":true,"proxy":false}}}""", "VPN/proxy detected");
        Check(200, """{"status":"warning","192.0.2.1":{"detections":{"vpn":false,"proxy":false}}}""", "VPN/proxy not detected");
        Check(200, """{"status":"ok","192.0.2.1":{"detections":{"vpn":"false"}}}""", "Unknown response format");
        Check(200, """{"status":"ok","192.0.2.2":{"detections":{"vpn":false}}}""", "Unknown response format");
        Check(200, "{}", "Unknown response format");
        Check(200, "[]", "Unknown response format");
        Check(200, "not json", "Unknown response format");
        Check(429, "{}", "ProxyCheck rate limit reached");
        Check(429, """{"message":"Daily allowance exhausted"}""", "ProxyCheck quota exceeded");
        Check(503, "{}", "Reputation request failed");
    }
}
