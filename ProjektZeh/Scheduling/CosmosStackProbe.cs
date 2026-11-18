using System.Runtime.InteropServices;

namespace ProjektZeh.Scheduling;

/// <summary>
/// Stackprobe fuer <see cref="ThreadStarter"/>: __cosmos_heap_alloc ist der Export von Cosmos HeapNative.Alloc und fuehrt wie
/// SchedulerThread.InitializeStack ueber MemoryOp.Alloc in Heap.Alloc (Cosmos 3.0.97). Liefert er null, wuerde der
/// Threadstart an derselben Stelle scheitern.
/// </summary>
internal static unsafe class CosmosStackProbe
{
    public static bool CanAllocate(int bytes)
    {
        void* block = HeapAlloc((nuint)bytes);
        if (block == null) return false;
        HeapFree(block);
        return true;
    }

    [DllImport("*", EntryPoint = "__cosmos_heap_alloc", ExactSpelling = true)]
    private static extern void* HeapAlloc(nuint size);

    [DllImport("*", EntryPoint = "__cosmos_heap_free", ExactSpelling = true)]
    private static extern void HeapFree(void* pointer);
}
