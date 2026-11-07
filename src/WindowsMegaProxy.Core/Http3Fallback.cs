using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowsMegaProxy;

public static class Http3Fallback
{
    public static bool IsUnavailable(IEnumerable<string> messages)
    {
        string text = string.Join('\n', messages).ToLowerInvariant();
        if (new[] { "certificate", "x509", "authentication", "407", "403", "access denied", "tls alert" }.Any(text.Contains)) return false;
        // Classify only known errors from the pinned core; unknown failures never downgrade the transport.
        return text.Contains("http/3 unavailable") && new[] { "no recent network activity", "network is unreachable", "connection refused" }.Any(text.Contains)
            || text.Contains("method not allowed") || text.Contains("unexpected status: 501");
    }
}
