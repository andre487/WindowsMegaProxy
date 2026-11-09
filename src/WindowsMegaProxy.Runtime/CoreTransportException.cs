using System.IO;

namespace WindowsMegaProxy;

// Only retry known transport failures; authentication and trust errors need user action.
public sealed class CoreTransportException(string message) : IOException(message);
