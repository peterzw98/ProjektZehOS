using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmos.Kernel.Core.IO;
using Cosmos.Kernel.Core.Memory;
using Cosmos.Kernel.Core.Memory.GarbageCollector;
using Cosmos.Kernel.Core.Runtime.ExceptionHandling;
using Internal.Runtime;

namespace Cosmos.Kernel.Core.Runtime;


internal static unsafe class Memory
{
    [RuntimeExport("RhNewArray")]
    internal static unsafe void* RhNewArray(MethodTable* pEEType, int length)
    {
        // ZehOS patch gc-oom-throw, see PATCHES.md
        // Cast to uint, a negative length would surface as OutOfMemoryException; the other int
        // helpers throw OverflowException. RhAllocateNewArray takes a uint, its CoreLib callers
        // (GC.AllocateNewArray) reject a negative length before the call.
        if (length < 0)
        {
            ThrowOverflow();
        }

        RhAllocateNewArray(pEEType, (uint)length, 0, out void* result);
        return result;
    }

    [RuntimeExport("RhAllocateNewArray")]
    internal static unsafe void RhAllocateNewArray(MethodTable* pArrayEEType, uint numElements, GC_ALLOC_FLAGS flags,
        out void* pResult)
    {
        // ZehOS patch gc-oom-throw, see PATCHES.md
        uint size = ArraySize(pArrayEEType, numElements);

        GCObject* result = AllocObject(size, pArrayEEType, (int)numElements, flags);

        pResult = result;
    }

    [RuntimeExport("RhpNewArray")]
    internal static unsafe void* RhpNewArray(MethodTable* pMT, int length)
    {
        // ZehOS patch gc-oom-throw, see PATCHES.md
        if (length < 0)
        {
            ThrowOverflow();
        }

        uint size = ArraySize(pMT, (uint)length);

        GCObject* result = AllocObject(size, pMT, length);

        return result;
    }

    [RuntimeExport("RhpNewPtrArrayFast")]
    internal static unsafe void* RhpNewPtrArrayFast(MethodTable* pMT, int length)
    {
        // ZehOS patch gc-oom-throw, see PATCHES.md
        if (length < 0)
        {
            ThrowOverflow();
        }

        uint size = ArraySize(pMT, (uint)length);

        GCObject* result = AllocObject(size, pMT, length);

        return result;
    }
    [RuntimeExport("RhpNewArrayFast")]
    internal static unsafe void* RhpNewArrayFast(MethodTable* pMT, int length)
    {
        // ZehOS patch gc-oom-throw, see PATCHES.md
        if (length < 0)
        {
            ThrowOverflow();
        }

        uint size = ArraySize(pMT, (uint)length);

        GCObject* result = AllocObject(size, pMT, length);

        return result;
    }

    [RuntimeExport("RhNewVariableSizeObject")]
    internal static unsafe void* RhNewVariableSizeObject(MethodTable* pEEType, int length)
    {
        return RhpNewArray(pEEType, length);
    }

    [RuntimeExport("RhAllocateNewObject")]
    internal static unsafe void RhAllocateNewObject(MethodTable* pEEType, GC_ALLOC_FLAGS flags, void* pResult)
    {
        GCObject* result = AllocObject(pEEType->RawBaseSize, pEEType, 0, flags);

        *(void**)pResult = result;
        // as some point we should set flags   
    }

    [RuntimeExport("RhpGcSafeZeroMemory")]
    internal static unsafe byte* RhpGcSafeZeroMemory(byte* dmem, nuint size)
    {
        MemoryOp.MemSet(dmem, 0, (int)size);
        return dmem;
    }

    [RuntimeExport("RhpNewFast")]
    internal static unsafe void* RhpNewFast(MethodTable* pMT)
    {
        // Use RawBaseSize instead of BaseSize because BaseSize only works for canonical/array types
        // For generic type definitions, BaseSize contains parameter count, not the actual size
        GCObject* result = AllocObject(pMT->RawBaseSize, pMT, 0);
        return (byte*)result;
    }

    [RuntimeExport("RhSpanHelpers_MemCopy")]
    internal static unsafe void RhSpanHelpers_MemCopy(byte* dest, byte* src, UIntPtr len)
    {
        MemoryOp.MemCopy(dest, src, (int)len);
    }

    // memmove and memset return dest, as in C: the C code linked in relies on
    // it (clang tail-calls memset from uACPI's zeroing allocator and hands the
    // result back as the allocation).
    [RuntimeExport("memmove")]
    internal static unsafe byte* memmove(byte* dest, byte* src, UIntPtr len)
    {
        MemoryOp.MemMove(dest, src, (int)len);
        return dest;
    }

    [RuntimeExport("memset")]
    internal static unsafe byte* memset(byte* dest, int value, UIntPtr len)
    {
        MemoryOp.MemSet(dest, (byte)value, (int)len);
        return dest;
    }

    [RuntimeExport("RhNewString")]
    internal static unsafe void* RhNewString(MethodTable* pEEType, int length)
    {
        return RhpNewArray(pEEType, length);
    }

    [RuntimeExport("RhRegisterFrozenSegment")]
    internal static unsafe IntPtr RhRegisterFrozenSegment(void* pSegmentStart, nuint allocSize, nuint commitSize, nuint reservedSize)
    {
        if (GarbageCollector.IsEnabled)
        {
            return GarbageCollector.RegisterFrozenSegment((IntPtr)pSegmentStart, allocSize, commitSize, reservedSize);
        }
        else
        {
            return (IntPtr)pSegmentStart;
        }
    }

    [RuntimeExport("RhUpdateFrozenSegment")]
    internal static unsafe void RhUpdateFrozenSegment(IntPtr seg, void* allocated, void* committed)
    {
        if (GarbageCollector.IsEnabled)
        {
            GarbageCollector.UpdateFrozenSegment((nint)seg, (nint)allocated, (nint)committed);
        }
    }

    [RuntimeExport("RhHandleSet")]
    internal static void RhHandleSet(IntPtr handle, GCObject* obj)
    {
        GarbageCollector.HandleSetPrimary(handle, obj);
    }

    [RuntimeExport("RhHandleFree")]
    internal static void RhHandleFree(IntPtr handle)
    {
        GarbageCollector.FreeHandle(handle);
    }

    [RuntimeExport("RhpHandleAlloc")]
    internal static IntPtr RhpHandleAlloc(GCObject* obj, GCHandleType handleType)
    {
        return GarbageCollector.AllocateHandler(obj, handleType, IntPtr.Zero);
    }

    [RuntimeExport("RhpHandleAllocDependent")]
    internal static IntPtr RhpHandleAllocDependent(GCObject* primary, GCObject* secondary)
    {
        DepDiag.Record();
        return GarbageCollector.AllocateHandler(primary, (GCHandleType)6, (nint)secondary);
    }

    // DIAG dep-handle (nie mergen): Aufrufer der Dependent-Handle-Anlage zaehlen (IP nach 1, 2, 3 Entrollschritten)
    internal static unsafe class DepDiag
    {
        private const int Slots = 24;
        private struct Tab { public fixed ulong Key[Slots]; public fixed ulong Ip1[Slots]; public fixed ulong Ip3[Slots]; public fixed ulong Cnt[Slots]; }
        private static Tab s_t;
        internal static ulong Total;
        internal static ulong Frees;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Record()
        {
            Interlocked.Increment(ref Total);
            REGDISPLAY rd = default;
            nuint ip = Cosmos.Kernel.Core.Bridge.ContextSwitchNative.CaptureRegDisplay(&rd);
            UnwindState st = default;
            ExceptionHelper.SeedUnwindStateFromRegDisplay(ref st, &rd, ip);
            nuint ip1 = 0, ip2 = 0, ip3 = 0;
            if (!ExceptionHelper.UnwindOneFrameWithCFI(ref st, ip)) { Add(0, 0, 0); return; }
            ip1 = st.ReturnAddress;
            if (ExceptionHelper.UnwindOneFrameWithCFI(ref st, ip1))
            {
                ip2 = st.ReturnAddress;
                if (ExceptionHelper.UnwindOneFrameWithCFI(ref st, ip2)) ip3 = st.ReturnAddress;
            }
            Add(ip2, ip1, ip3);
        }

        private static void Add(nuint key, nuint ip1, nuint ip3)
        {
            for (int i = 0; i < Slots; i++)
            {
                ulong k = s_t.Key[i];
                if (k == 0 && key != 0)
                {
                    k = (ulong)Interlocked.CompareExchange(ref *(long*)&s_t.Key[i], (long)key, 0);
                    if (k == 0) { s_t.Ip1[i] = ip1; s_t.Ip3[i] = ip3; k = key; }
                }
                if (k == key) { Interlocked.Increment(ref *(long*)&s_t.Cnt[i]); return; }
            }
        }

        internal static void Dump()
        {
            Serial.WriteString("[GC] DEPH allocs=");
            Serial.WriteNumber((uint)Total);
            Serial.WriteString(" frees=");
            Serial.WriteNumber((uint)Frees);
            Serial.WriteString("\n");
            for (int i = 0; i < Slots; i++)
            {
                if (s_t.Key[i] == 0) continue;
                Serial.WriteString("[GC] DEPH caller n=");
                Serial.WriteNumber((uint)s_t.Cnt[i]);
                Serial.WriteString(" ip2=");
                Serial.WriteHexWithPrefix(s_t.Key[i]);
                Serial.WriteString(" ip1=");
                Serial.WriteHexWithPrefix(s_t.Ip1[i]);
                Serial.WriteString(" ip3=");
                Serial.WriteHexWithPrefix(s_t.Ip3[i]);
                Serial.WriteString("\n");
            }
        }
    }

    // ZehOS patch gc-oom-throw, see PATCHES.md
    // An allocation helper never returns null: compiled code uses the result of `newobj`/`newarr`
    // without a check, so a null became a page fault at a small address or, through a span over
    // it, a silent "destination too short". As in .NET, a failed allocation throws
    // OutOfMemoryException, a negative array length OverflowException.

    /// <summary>
    /// Largest object the helpers hand to the collector. .NET's limit is just under 2 GB as well; it
    /// also keeps the collector's 32-bit size arithmetic (Align, size classes) from wrapping.
    /// </summary>
    private const ulong MaxObjectSize = int.MaxValue;

    /// <summary>
    /// Thrown when even the OutOfMemoryException cannot be allocated. Created at boot by
    /// <see cref="PreallocateOutOfMemoryException"/>.
    /// </summary>
    private static OutOfMemoryException? s_preallocatedOutOfMemory;

    /// <summary>Non-zero while a CPU allocates a fresh OutOfMemoryException.</summary>
    private static int s_allocatingOutOfMemory;

    /// <summary>
    /// Creates the instance thrown when the heap cannot hold a new OutOfMemoryException. Called once
    /// at boot, after the managed modules (static bases) are initialized.
    /// </summary>
    internal static void PreallocateOutOfMemoryException()
    {
        s_preallocatedOutOfMemory ??= new OutOfMemoryException();
    }

    /// <summary>
    /// Size of an array or string object with <paramref name="length"/> components. Computed in 64 bits:
    /// in 32 bits a long[] of 2^29 elements wrapped to a small object whose Length still allowed
    /// writes far past it. Throws OutOfMemoryException above <see cref="MaxObjectSize"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ArraySize(MethodTable* pMT, uint length)
    {
        ulong size = pMT->BaseSize + (ulong)length * pMT->ComponentSize;
        if (size > MaxObjectSize)
        {
            ThrowOutOfMemory();
        }

        return (uint)size;
    }

    /// <summary>
    /// Throws OutOfMemoryException. A fresh instance where the heap still holds one (a shared instance
    /// would collect every throw's stack trace); the preallocated one if allocating it failed, or another
    /// CPU is allocating its own at the same moment. Without that fallback, the failed `new` would
    /// re-enter here without end.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowOutOfMemory()
    {
        if (Interlocked.CompareExchange(ref s_allocatingOutOfMemory, 1, 0) != 0)
        {
            OutOfMemoryException? preallocated = s_preallocatedOutOfMemory;
            if (preallocated is null)
            {
                ExceptionHelper.FailFast("Out of memory before the OutOfMemoryException was preallocated");
            }

            StackTraceMetadata.GetStackTraceString(preallocated!) = null;
            throw preallocated!;
        }

        OutOfMemoryException fresh;
        try
        {
            fresh = new OutOfMemoryException();
        }
        finally
        {
            Volatile.Write(ref s_allocatingOutOfMemory, 0);
        }

        throw fresh;
    }

    /// <summary>Throws OverflowException for a negative array length, as .NET's RhpNewArray does.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowOverflow()
    {
        throw new OverflowException();
    }

    /// <summary>
    /// Allocates an object and stores its header: the MethodTable, and the Length of an array or
    /// a string. With the GC enabled both are stored before interrupts can fire again (see
    /// <see cref="GarbageCollector.AllocObject"/>), so no other thread ever collects while the new
    /// object is still a headerless block.
    /// </summary>
    /// <param name="size">Object size in bytes.</param>
    /// <param name="pMT">The object's MethodTable.</param>
    /// <param name="length">Component count of an array or a string; ignored for any other type.</param>
    /// <param name="flags">Runtime allocation flags.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe GCObject* AllocObject(uint size, MethodTable* pMT, int length, GC_ALLOC_FLAGS flags = GC_ALLOC_FLAGS.GC_ALLOC_NO_FLAGS)
    {
        if (GarbageCollector.IsEnabled)
        {
            // ZehOS patch gc-oom-throw, see PATCHES.md
            GCObject* allocated = GarbageCollector.AllocObject((nint)size, pMT, length, flags);
            if (allocated == null)
            {
                ThrowOutOfMemory();
            }

            return allocated;
        }
        else
        {
            GCObject* result = (GCObject*)MemoryOp.Alloc(size);
            MemoryOp.MemSet((byte*)result, 0, (int)size);
            result->MethodTable = pMT;
            if (pMT->HasComponentSize)
            {
                result->Length = length;
            }

            return result;
        }
    }
}
