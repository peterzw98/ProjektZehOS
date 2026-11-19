#pragma warning disable IDE0011
using System;
using Cosmos.Kernel.Core.IO;
using Cosmos.Kernel.Core.Memory;
using CoreGC = Cosmos.Kernel.Core.Memory.GarbageCollector.GarbageCollector;

namespace Cosmos.Kernel.Tests.GarbageCollector;

// ZehOS E5-H (#538-Folgefrage), nur Probe, nie mergen: Woran scheitert new byte[1-8 MiB]
// bei ~316 MB im 512-MiB-Gast? Am Fehlschlag werden freie Seiten, laengster freier Lauf
// und die Belegung des RAT ausgegeben. Die Ausgabe allokiert nicht.
internal static unsafe class FragProbe
{
    private static ulong s_rng = 0x9E3779B97F4A7C15;

    private static ulong Next()
    {
        s_rng ^= s_rng << 13;
        s_rng ^= s_rng >> 7;
        s_rng ^= s_rng << 17;
        return s_rng;
    }

    private static int Opaque(int v) => v;

    public static void Dump(string tag, ulong liveBytes, int requested)
    {
        byte* rat = (byte*)PageAllocator.RatAddress;
        ulong total = PageAllocator.TotalPageCount;
        ulong longest = 0, run = 0, runs = 0, runsGe257 = 0, runsGe2049 = 0, runsLt16 = 0;
        ulong gcSegs = 0, gcSegsSmall = 0, gcSegPages = 0, curSeg = 0;
        for (ulong i = 0; i <= total; i++)
        {
            byte b = i < total ? rat[i] : (byte)0xFF;
            if (b == 0)
            {
                run++;
            }
            else
            {
                if (run > 0)
                {
                    runs++;
                    if (run > longest) longest = run;
                    if (run >= 257) runsGe257++;
                    if (run >= 2049) runsGe2049++;
                    if (run < 16) runsLt16++;
                }
                run = 0;
            }
            // GCHeap-Segment: Startseite Typ 1, danach Extension.
            if (i < total && b == 1)
            {
                if (curSeg > 0) { gcSegs++; gcSegPages += curSeg; if (curSeg <= 16) gcSegsSmall++; }
                curSeg = 1;
            }
            else if (curSeg > 0 && i < total && false)
            {
                curSeg++;
            }
            else if (curSeg > 0 && i < total && (PageType)b == PageType.Extension)
            {
                curSeg++;
            }
            else if (curSeg > 0)
            {
                gcSegs++; gcSegPages += curSeg; if (curSeg <= 16) gcSegsSmall++;
                curSeg = 0;
            }
        }

        PageAllocator.GetPageCountsByType(out ulong empty, out ulong gcHeap, out _, out _, out _,
            out ulong unmanaged, out _, out _, out _, out ulong ext, out _);

        Serial.WriteString("[E5H] "); Serial.WriteString(tag);
        Serial.WriteString(" requested="); Serial.WriteNumber((ulong)(uint)requested);
        Serial.WriteString(" needPages="); Serial.WriteNumber(((ulong)(uint)requested + 4095) / 4096 + 1);
        Serial.WriteString(" liveMiB="); Serial.WriteNumber(liveBytes >> 20);
        Serial.WriteString(" totalPages="); Serial.WriteNumber(total);
        Serial.WriteString(" freePages="); Serial.WriteNumber(PageAllocator.FreePageCount);
        Serial.WriteString(" freeMiB="); Serial.WriteNumber(PageAllocator.FreePageCount * 4096 >> 20);
        Serial.WriteString(" longestFreeRun="); Serial.WriteNumber(longest);
        Serial.WriteString(" freeRuns="); Serial.WriteNumber(runs);
        Serial.WriteString(" runs>=257="); Serial.WriteNumber(runsGe257);
        Serial.WriteString(" runs>=2049="); Serial.WriteNumber(runsGe2049);
        Serial.WriteString(" runs<16="); Serial.WriteNumber(runsLt16);
        Serial.WriteString(" gcSegs="); Serial.WriteNumber(gcSegs);
        Serial.WriteString(" gcSegs<=16p="); Serial.WriteNumber(gcSegsSmall);
        Serial.WriteString(" gcSegPages="); Serial.WriteNumber(gcSegPages);
        Serial.WriteString(" unmanagedStarts="); Serial.WriteNumber(unmanaged);
        Serial.WriteString(" ext="); Serial.WriteNumber(ext);
        Serial.WriteString("\n");

        // Karte: je 1 MiB (256 Seiten) ein Zeichen, Anteil freier Seiten 0-9, '#' voll belegt,
        // '.' ganz frei. Unten = Adresse niedrig.
        Serial.WriteString("[E5H] map ");
        for (ulong c = 0; c < total; c += 256)
        {
            ulong free = 0, n = 0;
            for (ulong i = c; i < c + 256 && i < total; i++, n++)
            {
                if (rat[i] == 0) free++;
            }
            byte ch = free == n ? (byte)'.' : free == 0 ? (byte)'#' : (byte)('0' + free * 10 / (n + 1));
            Serial.ComWrite(ch);
        }
        Serial.WriteString("\n");
    }

    // Szenario A: alle Arrays halten, Groessen 1-8 MiB zufaellig, bis OOM.
    public static void KeepAll()
    {
        byte[]?[] hold = new byte[]?[1024];
        int n = 0;
        ulong live = 0;
        CoreGC.Collect();
        Dump("A-start", 0, 0);
        while (n < hold.Length)
        {
            int size = Opaque((int)((1 + Next() % 8) << 20));
            try
            {
                byte[] a = new byte[size];
                a[0] = 1; a[size - 1] = 2;
                hold[n++] = a;
                live += (ulong)size;
            }
            catch (OutOfMemoryException)
            {
                Dump("A-oom", live, size);
                break;
            }
        }
        for (int i = 0; i < hold.Length; i++) hold[i] = null;
        CoreGC.Collect();
        Dump("A-end", 0, 0);
    }

    // Szenario B: Wechsellast. Ein Ring von Slots mit 1-8 MiB-Arrays, jeder Schritt ersetzt
    // einen Slot; dazwischen kleine Objekte, von denen ein Teil ueberlebt (wie Puffer,
    // Strings, Listen einer Anwendung). Laeuft bis OOM oder Schrittgrenze.
    public static void Churn(int slots, int smallKeepEvery, string tag)
    {
        byte[]?[] ring = new byte[]?[slots];
        object?[] smallKeep = new object?[4096];
        int smallN = 0;
        ulong live = 0;
        CoreGC.Collect();
        Dump(tag + "-start", 0, 0);
        int step = 0;
        for (; step < 4000; step++)
        {
            int slot = (int)(Next() % (ulong)slots);
            int size = Opaque((int)((1 + Next() % 8) << 20));
            if (ring[slot] != null) { live -= (ulong)ring[slot]!.Length; ring[slot] = null; }
            // Kleine Objekte: 64 Stueck, jedes smallKeepEvery-te bleibt lebendig.
            for (int k = 0; k < 64; k++)
            {
                object o = new byte[Opaque(32 + (int)(Next() % 2000))];
                if (smallKeepEvery > 0 && (k % smallKeepEvery) == 0)
                {
                    smallKeep[smallN % smallKeep.Length] = o;
                    smallN++;
                }
            }
            try
            {
                byte[] a = new byte[size];
                a[0] = 1; a[size - 1] = 2;
                ring[slot] = a;
                live += (ulong)size;
            }
            catch (OutOfMemoryException)
            {
                Serial.WriteString("[E5H] "); Serial.WriteString(tag); Serial.WriteString(" step=");
                Serial.WriteNumber((ulong)step); Serial.WriteString("\n");
                Dump(tag + "-oom", live, size);
                break;
            }
            if (step % 500 == 0)
            {
                Serial.WriteString("[E5H] "); Serial.WriteString(tag); Serial.WriteString(" step=");
                Serial.WriteNumber((ulong)step); Serial.WriteString("\n");
                Dump(tag + "-step", live, size);
            }
        }
        if (step == 4000) Dump(tag + "-noOOM", live, 0);
        for (int i = 0; i < ring.Length; i++) ring[i] = null;
        for (int i = 0; i < smallKeep.Length; i++) smallKeep[i] = null;
        CoreGC.Collect();
        Dump(tag + "-end", 0, 0);
    }

    public static void Run()
    {
        KeepAll();
        Churn(40, 0, "B40");
        Churn(40, 8, "C40k8");
        Churn(56, 8, "D56k8");
        Serial.WriteString("[E5H] done\n");
    }
}
