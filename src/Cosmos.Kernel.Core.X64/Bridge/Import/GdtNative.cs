using System.Runtime.InteropServices;

namespace Cosmos.Kernel.Core.X64.Bridge;

// ZehOS patch user-gdt, see PATCHES.md
internal static unsafe partial class GdtNative
{
    /// <summary>Loads the GDT, reloads CS, SS, DS and ES, and loads TR; interrupts masked by the caller.</summary>
    [LibraryImport("*", EntryPoint = "_native_x64_load_gdt")]
    [SuppressGCTransition]
    public static partial void LoadGdt(void* ptr, uint tssSelector);

    [LibraryImport("*", EntryPoint = "_native_x64_store_gdt")]
    [SuppressGCTransition]
    public static partial void StoreGdt(void* ptr);

    /// <summary>CS in bits 15:0, SS in 31:16, TR in 47:32.</summary>
    [LibraryImport("*", EntryPoint = "_native_x64_get_selectors")]
    [SuppressGCTransition]
    public static partial ulong GetSelectors();
}
