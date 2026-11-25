using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Shared;

namespace ProjektZeh.Tests.Processes;

[TestClass]
public sealed class AddressSpaceMapTests
{
    private const ulong Page = 4096;
    private const ulong Gib = 1UL << 30;
    private const ulong Tib = 1UL << 40;
    private const ulong Base = 0x1000;
    private const ulong Top = 0x7FFF_FFFF_F000;

    private static AddressSpaceMap Create() => new(Base, Top);

    private static void Fixed(AddressSpaceMap map, ulong address, ulong pages, MapAccess access = MapAccess.ReadWrite)
    {
        ResultObject<ulong> r = map.Reserve(address, pages * Page, access, Placement.FixedReplace);
        Assert.IsTrue(r.IsOK());
        Assert.AreEqual(address, r.Value);
    }

    private static void AssertAccess(AddressSpaceMap map, ulong address, MapAccess? expected)
    {
        bool found = map.TryFind(address, out MapAccess access);
        Assert.AreEqual(expected.HasValue, found, $"Adresse {address:X}");
        if (expected.HasValue)
        {
            Assert.AreEqual(expected.Value, access, $"Adresse {address:X}");
        }
    }

    [TestMethod]
    public void ConstructorUnalignedOrEmptyThrows()
    {
        Assert.ThrowsException<ArgumentException>(() => new AddressSpaceMap(1, Page * 4));
        Assert.ThrowsException<ArgumentException>(() => new AddressSpaceMap(Page, Page + 1));
        Assert.ThrowsException<ArgumentException>(() => new AddressSpaceMap(Page * 4, Page * 4));
        Assert.ThrowsException<ArgumentException>(() => new AddressSpaceMap(Page * 8, Page * 4));
    }

    [TestMethod]
    public void ReserveHintReturnsHintWhenFree()
    {
        AddressSpaceMap map = Create();
        ResultObject<ulong> r = map.Reserve(0x10_0000, 3 * Page, MapAccess.Read, Placement.Hint);
        Assert.IsTrue(r.IsOK());
        Assert.AreEqual(0x10_0000UL, r.Value);
        AssertAccess(map, 0x10_0000 + 2 * Page, MapAccess.Read);
        AssertAccess(map, 0x10_0000 + 3 * Page, null);
    }

    [TestMethod]
    public void ReserveHintZeroStartsAtBeginning()
    {
        AddressSpaceMap map = Create();
        Assert.AreEqual(Base, map.Reserve(0, Page, MapAccess.ReadWrite, Placement.Hint).Value);
    }

    [TestMethod]
    public void ReserveHintOccupiedMovesToNextFreeSpot()
    {
        AddressSpaceMap map = Create();
        Fixed(map, 0x10_0000, 4);
        Fixed(map, 0x10_5000, 1);
        // Lücke von einer Seite bei 0x104000 ist zu klein für zwei Seiten
        ResultObject<ulong> r = map.Reserve(0x10_1000, 2 * Page, MapAccess.ReadWrite, Placement.Hint);
        Assert.IsTrue(r.IsOK());
        Assert.AreEqual(0x10_6000UL, r.Value);
        ResultObject<ulong> one = map.Reserve(0x10_1000, Page, MapAccess.ReadWrite, Placement.Hint);
        Assert.AreEqual(0x10_4000UL, one.Value);
    }

    [TestMethod]
    public void ReserveHintWrapsToBeginningWhenTopIsFull()
    {
        var map = new AddressSpaceMap(Page, 8 * Page);
        Fixed(map, 4 * Page, 4);
        ResultObject<ulong> r = map.Reserve(5 * Page, 2 * Page, MapAccess.ReadWrite, Placement.Hint);
        Assert.IsTrue(r.IsOK());
        Assert.AreEqual(Page, r.Value);
    }

    [TestMethod]
    public void ReserveHintNoSpaceFailsUnchanged()
    {
        var map = new AddressSpaceMap(Page, 5 * Page);
        Fixed(map, 2 * Page, 1);
        ResultObject<ulong> r = map.Reserve(0, 3 * Page, MapAccess.ReadWrite, Placement.Hint);
        Assert.IsFalse(r.IsOK());
        Assert.AreEqual(ResultCode.Failure, r.Status.Code);
        AssertAccess(map, Page, null);
        AssertAccess(map, 3 * Page, null);
        Assert.IsFalse(map.Reserve(0, 5 * Page, MapAccess.ReadWrite, Placement.Hint).IsOK());
    }

    [TestMethod]
    public void ReserveFixedReplaceReplacesOverlap()
    {
        AddressSpaceMap map = Create();
        Fixed(map, 0x10_0000, 8, MapAccess.Read);
        Fixed(map, 0x10_2000, 2, MapAccess.ReadExecute);
        AssertAccess(map, 0x10_1000, MapAccess.Read);
        AssertAccess(map, 0x10_2000, MapAccess.ReadExecute);
        AssertAccess(map, 0x10_3000, MapAccess.ReadExecute);
        AssertAccess(map, 0x10_4000, MapAccess.Read);
        AssertAccess(map, 0x10_7000, MapAccess.Read);
        AssertAccess(map, 0x10_8000, null);
    }

    [TestMethod]
    public void ReserveFixedNoReplaceOverlapFailsAndChangesNothing()
    {
        AddressSpaceMap map = Create();
        Fixed(map, 0x10_0000, 4, MapAccess.Read);
        ResultObject<ulong> r = map.Reserve(0x10_3000, 4 * Page, MapAccess.ReadWrite, Placement.FixedNoReplace);
        Assert.IsFalse(r.IsOK());
        AssertAccess(map, 0x10_3000, MapAccess.Read);
        AssertAccess(map, 0x10_4000, null);
        Assert.IsTrue(map.Reserve(0x10_4000, Page, MapAccess.ReadWrite, Placement.FixedNoReplace).IsOK());
        Assert.IsFalse(map.Reserve(0xF_F000, 2 * Page, MapAccess.ReadWrite, Placement.FixedNoReplace).IsOK());
    }

    [TestMethod]
    public void ReleaseSplitsAtStartMiddleAndEnd()
    {
        AddressSpaceMap map = Create();
        Fixed(map, 0x10_0000, 10);
        Assert.IsTrue(map.Release(0x10_0000, Page).IsOK());      // Anfang
        Assert.IsTrue(map.Release(0x10_4000, 2 * Page).IsOK());  // Mitte
        Assert.IsTrue(map.Release(0x10_9000, Page).IsOK());      // Ende
        AssertAccess(map, 0x10_0000, null);
        AssertAccess(map, 0x10_1000, MapAccess.ReadWrite);
        AssertAccess(map, 0x10_3000, MapAccess.ReadWrite);
        AssertAccess(map, 0x10_4000, null);
        AssertAccess(map, 0x10_5000, null);
        AssertAccess(map, 0x10_6000, MapAccess.ReadWrite);
        AssertAccess(map, 0x10_8000, MapAccess.ReadWrite);
        AssertAccess(map, 0x10_9000, null);
    }

    [TestMethod]
    public void ReleaseWithGapsAndAcrossRegionsSucceeds()
    {
        AddressSpaceMap map = Create();
        Fixed(map, 0x10_0000, 2);
        Fixed(map, 0x10_5000, 2);
        Assert.IsTrue(map.Release(0xF_0000, 0x20_0000).IsOK());
        AssertAccess(map, 0x10_0000, null);
        AssertAccess(map, 0x10_5000, null);
        Assert.IsTrue(map.Release(0x30_0000, Page).IsOK());
    }

    [TestMethod]
    public void ProtectChangesPartAndSplits()
    {
        AddressSpaceMap map = Create();
        Fixed(map, 0x10_0000, 6);
        Assert.IsTrue(map.Protect(0x10_2000, 2 * Page, MapAccess.None).IsOK());
        AssertAccess(map, 0x10_1000, MapAccess.ReadWrite);
        AssertAccess(map, 0x10_2000, MapAccess.None);
        AssertAccess(map, 0x10_3000, MapAccess.None);
        AssertAccess(map, 0x10_4000, MapAccess.ReadWrite);
    }

    [TestMethod]
    public void ProtectAcrossAdjacentRegionsSucceeds()
    {
        AddressSpaceMap map = Create();
        Fixed(map, 0x10_0000, 2, MapAccess.Read);
        Fixed(map, 0x10_2000, 2, MapAccess.ReadExecute);
        Assert.IsTrue(map.Protect(0x10_1000, 2 * Page, MapAccess.ReadWrite).IsOK());
        AssertAccess(map, 0x10_0000, MapAccess.Read);
        AssertAccess(map, 0x10_1000, MapAccess.ReadWrite);
        AssertAccess(map, 0x10_2000, MapAccess.ReadWrite);
        AssertAccess(map, 0x10_3000, MapAccess.ReadExecute);
    }

    [TestMethod]
    public void ProtectGapFailsAndChangesNothing()
    {
        AddressSpaceMap map = Create();
        Fixed(map, 0x10_0000, 2, MapAccess.Read);
        Fixed(map, 0x10_3000, 2, MapAccess.Read);
        ResultErr r = map.Protect(0x10_0000, 5 * Page, MapAccess.ReadWrite);
        Assert.IsFalse(r.IsOK());
        AssertAccess(map, 0x10_0000, MapAccess.Read);
        AssertAccess(map, 0x10_1000, MapAccess.Read);
        AssertAccess(map, 0x10_2000, null);
        AssertAccess(map, 0x10_3000, MapAccess.Read);
        Assert.IsFalse(map.Protect(0x20_0000, Page, MapAccess.Read).IsOK());
        Assert.IsFalse(map.Protect(0xF_F000, 2 * Page, MapAccess.Read).IsOK());
    }

    [TestMethod]
    public void OperationsAddressPlusLengthOverflowAreRejected()
    {
        AddressSpaceMap map = Create();
        ulong address = 0xFFFF_FFFF_FFFF_F000;
        Assert.IsFalse(map.Reserve(address, 2 * Page, MapAccess.Read, Placement.FixedReplace).IsOK());
        Assert.IsFalse(map.Reserve(address, 2 * Page, MapAccess.Read, Placement.FixedNoReplace).IsOK());
        Assert.IsFalse(map.Release(address, 2 * Page).IsOK());
        Assert.IsFalse(map.Protect(address, 2 * Page, MapAccess.Read).IsOK());
        Assert.IsFalse(map.Release(Page, 0xFFFF_FFFF_FFFF_F000).IsOK());
        Assert.IsFalse(map.Reserve(0, 0xFFFF_FFFF_FFFF_F000, MapAccess.Read, Placement.Hint).IsOK());
    }

    [TestMethod]
    public void OperationsOutsideRangeAreRejected()
    {
        AddressSpaceMap map = Create();
        Assert.IsFalse(map.Reserve(0, Page, MapAccess.Read, Placement.FixedReplace).IsOK());
        Assert.IsFalse(map.Reserve(Top - Page, 2 * Page, MapAccess.Read, Placement.FixedReplace).IsOK());
        Assert.IsTrue(map.Reserve(Top - Page, Page, MapAccess.Read, Placement.FixedReplace).IsOK());
        Assert.IsFalse(map.Release(Top, Page).IsOK());
    }

    [TestMethod]
    public void OperationsUnalignedValuesAreRejectedWithInvalidArgument()
    {
        AddressSpaceMap map = Create();
        foreach (Placement p in new[] { Placement.Hint, Placement.FixedReplace, Placement.FixedNoReplace })
        {
            ResultObject<ulong> a = map.Reserve(0x10_0001, Page, MapAccess.Read, p);
            Assert.AreEqual(ResultCode.InvalidArgument, a.Status.Code);
            ResultObject<ulong> b = map.Reserve(0x10_0000, Page + 1, MapAccess.Read, p);
            Assert.AreEqual(ResultCode.InvalidArgument, b.Status.Code);
        }

        Assert.AreEqual(ResultCode.InvalidArgument, map.Release(0x10_0001, Page).Code);
        Assert.AreEqual(ResultCode.InvalidArgument, map.Release(0x10_0000, 100).Code);
        Assert.AreEqual(ResultCode.InvalidArgument, map.Protect(0x10_0001, Page, MapAccess.Read).Code);
        AssertAccess(map, 0x10_0000, null);
    }

    [TestMethod]
    public void OperationsZeroLengthAreRejected()
    {
        AddressSpaceMap map = Create();
        Fixed(map, 0x10_0000, 1);
        foreach (Placement p in new[] { Placement.Hint, Placement.FixedReplace, Placement.FixedNoReplace })
        {
            Assert.AreEqual(ResultCode.InvalidArgument, map.Reserve(0x20_0000, 0, MapAccess.Read, p).Status.Code);
        }

        Assert.AreEqual(ResultCode.InvalidArgument, map.Release(0x10_0000, 0).Code);
        Assert.AreEqual(ResultCode.InvalidArgument, map.Protect(0x10_0000, 0, MapAccess.None).Code);
        AssertAccess(map, 0x10_0000, MapAccess.ReadWrite);
    }

    [TestMethod]
    public void TryFindBoundariesAndNoneAccess()
    {
        AddressSpaceMap map = Create();
        Assert.IsTrue(map.Reserve(0x10_0000, Page, MapAccess.None, Placement.FixedReplace).IsOK());
        AssertAccess(map, 0x10_0000, MapAccess.None);
        AssertAccess(map, 0x10_0FFF, MapAccess.None);
        AssertAccess(map, 0x10_1000, null);
        AssertAccess(map, 0xF_FFFF, null);
        AssertAccess(map, ulong.MaxValue, null);
    }

    [TestMethod]
    public void Sparse32GibAnd4TibPlus32GibAtTheSameTime()
    {
        AddressSpaceMap map = Create();
        ResultObject<ulong> a = map.Reserve(0, 32 * Gib, MapAccess.None, Placement.Hint);
        Assert.IsTrue(a.IsOK());
        ResultObject<ulong> b = map.Reserve(0, 4 * Tib + 32 * Gib, MapAccess.None, Placement.Hint);
        Assert.IsTrue(b.IsOK());
        Assert.IsTrue(b.Value >= a.Value + 32 * Gib);
        Assert.IsTrue(map.Protect(b.Value + 1000 * Page, Page, MapAccess.ReadWrite).IsOK());
        Assert.IsTrue(map.Protect(b.Value + 3 * Tib, Page, MapAccess.ReadWrite).IsOK());
        AssertAccess(map, b.Value + 1000 * Page, MapAccess.ReadWrite);
        AssertAccess(map, b.Value + 1001 * Page, MapAccess.None);
        AssertAccess(map, b.Value + 4 * Tib + 32 * Gib - 1, MapAccess.None);
        AssertAccess(map, b.Value + 4 * Tib + 32 * Gib, null);
        Assert.IsTrue(map.Release(a.Value, 32 * Gib).IsOK());
        AssertAccess(map, a.Value, null);
    }

    [TestMethod]
    public void Random10000OperationsMatchPageModel()
    {
        const int pages = 512;
        const ulong origin = 0x10_0000;
        var map = new AddressSpaceMap(origin, origin + pages * Page);
        MapAccess?[] model = new MapAccess?[pages];
        var random = new Random(20261124);
        MapAccess[] accesses = Enum.GetValues<MapAccess>();

        for (int step = 0; step < 10_000; step++)
        {
            int first = random.Next(pages);
            int count = 1 + random.Next(Math.Min(40, pages - first));
            ulong address = origin + (ulong)first * Page;
            ulong length = (ulong)count * Page;
            MapAccess access = accesses[random.Next(accesses.Length)];

            switch (random.Next(5))
            {
                case 0:
                    Assert.IsTrue(map.Reserve(address, length, access, Placement.FixedReplace).IsOK());
                    Array.Fill(model, access, first, count);
                    break;
                case 1:
                    bool free = Array.TrueForAll(model[first..(first + count)], m => m == null);
                    Assert.AreEqual(free, map.Reserve(address, length, access, Placement.FixedNoReplace).IsOK());
                    if (free)
                    {
                        Array.Fill(model, access, first, count);
                    }

                    break;
                case 2:
                    Assert.IsTrue(map.Release(address, length).IsOK());
                    Array.Fill(model, null, first, count);
                    break;
                case 3:
                    bool covered = Array.TrueForAll(model[first..(first + count)], m => m != null);
                    Assert.AreEqual(covered, map.Protect(address, length, access).IsOK());
                    if (covered)
                    {
                        Array.Fill(model, access, first, count);
                    }

                    break;
                default:
                    int run = 0;
                    int expected = -1;
                    for (int i = 0; i < pages && expected < 0; i++)
                    {
                        run = model[i] == null ? run + 1 : 0;
                        if (run == count)
                        {
                            expected = i - count + 1;
                        }
                    }

                    ResultObject<ulong> r = map.Reserve(origin, length, access, Placement.Hint);
                    Assert.AreEqual(expected >= 0, r.IsOK());
                    if (expected >= 0)
                    {
                        Assert.AreEqual(origin + (ulong)expected * Page, r.Value);
                        Array.Fill(model, access, expected, count);
                    }

                    break;
            }

            if (step % 50 == 0 || step == 9_999)
            {
                for (int i = 0; i < pages; i++)
                {
                    AssertAccess(map, origin + (ulong)i * Page, model[i]);
                    AssertAccess(map, origin + (ulong)i * Page + Page - 1, model[i]);
                }
            }
        }
    }
}
