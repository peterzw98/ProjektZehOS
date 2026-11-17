using System;

// Nur die Hosttests binden diese Ersatztypen ein; Cosmos-Hardware und Kernelprotokoll fehlen dort absichtlich.
namespace Cosmos.Kernel.System.Diagnostics
{
    internal static class Log
    {
        internal static Action<string>? Writer { get; set; }
        public static void WriteString(string text) => Writer?.Invoke(text);
    }
}

namespace ProjektZeh.Security
{
    internal static class CosmosSaltSource
    {
        public static void Fill(byte[] bytes) => throw new NotSupportedException("Hardwarezufall ist kein Teil dieses Hosttests.");
    }
}
namespace ProjektZeh.Runtime
{
    internal static class CosmosSystemInfo
    {
        public static (ulong Total, ulong Free) PhysicalMemory() => throw new NotSupportedException("Physischer Speicher fehlt im Hosttest.");
    }
}
