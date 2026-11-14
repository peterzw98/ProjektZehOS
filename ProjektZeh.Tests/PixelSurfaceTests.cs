using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Graphics;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class PixelSurfaceTests
{
    [TestMethod]
    public void Rectangle_NegativeAndOverflowingCoordinates_OnlyTouchVisiblePixels()
    {
        var pixels = new int[12];
        var surface = new PixelSurface(4, 3, pixels);
        surface.FillRectangle(-2, -1, 4, 3, 7);
        surface.FillRectangle(int.MaxValue, 0, int.MaxValue, 2, 8);
        surface.FillRectangle(int.MinValue, 0, int.MaxValue, 2, 8);
        CollectionAssert.AreEqual(new[] { 7, 7, 0, 0, 7, 7, 0, 0, 0, 0, 0, 0 }, pixels);
    }

    [TestMethod]
    public void Clip_AllPrimitives_RespectRestrictedArea()
    {
        var pixels = new int[16];
        var surface = new PixelSurface(4, 4, pixels);
        surface.SetClip(1, 1, 2, 2);
        surface.FillRectangle(0, 0, 4, 4, 1);
        surface.DrawLine(-100, 2, 100, 2, 2);
        surface.DrawPixels(new[] { 9, 8, 7, 6 }, 0, 0, 2, 2);
        CollectionAssert.AreEqual(new[] { 0, 0, 0, 0, 0, 6, 1, 0, 0, 2, 2, 0, 0, 0, 0, 0 }, pixels);
        surface.ResetClip();
        surface.FillRectangle(0, 0, 1, 1, 3);
        Assert.AreEqual(3, pixels[0]);
    }

    [DataTestMethod]
    [DataRow(-10, -10, -1, -1)]
    [DataRow(6, -10, 6, 100)]
    [DataRow(int.MaxValue, int.MaxValue, int.MinValue, int.MaxValue)]
    public void Line_EntirelyOutside_DoesNotCreateEdgeArtifacts(int x1, int y1, int x2, int y2)
    {
        var pixels = new int[12];
        new PixelSurface(4, 3, pixels).DrawLine(x1, y1, x2, y2, 4);
        CollectionAssert.AreEqual(new int[12], pixels);
    }

    [TestMethod]
    public void Line_ExtremeEndpoints_ClipsBeforeRasterizing()
    {
        var pixels = new int[12];
        var surface = new PixelSurface(4, 3, pixels);
        surface.DrawLine(int.MinValue, 1, int.MaxValue, 1, 4);
        surface.DrawLine(-2, -2, 4, 4, 5);
        CollectionAssert.AreEqual(new[] { 5, 0, 0, 0, 4, 5, 4, 4, 0, 0, 5, 0 }, pixels);
    }

    [TestMethod]
    public void Pixels_ClippedCopy_UsesCorrectSourceOffset()
    {
        var pixels = new int[6];
        new PixelSurface(3, 2, pixels).DrawPixels(new[] { 1, 2, 3, 4, 5, 6 }, -1, -1, 3, 2);
        CollectionAssert.AreEqual(new[] { 5, 6, 0, 0, 0, 0 }, pixels);
    }

    [TestMethod]
    public void Pixels_InvalidSource_IsRejectedBeforeDrawing()
    {
        var pixels = new int[6];
        var surface = new PixelSurface(3, 2, pixels);
        Assert.ThrowsException<ArgumentException>(() => surface.DrawPixels(new[] { 1 }, 0, 0, 3, 2));
        Assert.ThrowsException<ArgumentException>(() => surface.DrawPixels(new[] { 1 }, 0, 0, int.MaxValue, 2));
        CollectionAssert.AreEqual(new int[6], pixels);
    }

    [TestMethod]
    public void Text_NegativePositionAndUnicode_ClipGlyphAndUseFallback()
    {
        var font = new byte[512];
        font['A' * 2] = 0x80;
        font['A' * 2 + 1] = 0x40;
        font['?' * 2] = 0xc0;
        var pixels = new int[8];
        var surface = new PixelSurface(4, 2, pixels);
        surface.DrawText("A", -1, -1, 7, font, 2, 2);
        surface.DrawText("\u2603", 2, 0, 8, font, 2, 2);
        CollectionAssert.AreEqual(new[] { 7, 0, 8, 8, 0, 0, 0, 0 }, pixels);
    }

    [TestMethod]
    public void Constructor_InvalidDimensionsOrBuffer_RejectsUnsafeSurface()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new PixelSurface(0, 2, []));
        Assert.ThrowsException<ArgumentException>(() => new PixelSurface(int.MaxValue, 2, []));
        Assert.ThrowsException<ArgumentException>(() => new PixelSurface(3, 2, new int[5]));
    }

    [TestMethod]
    public void Text_OverflowingFontDimensions_RejectsInvalidFont()
    {
        var surface = new PixelSurface(2, 2, new int[4]);
        Assert.ThrowsException<ArgumentException>(() =>
            surface.DrawText("A", 0, 0, 1, new byte[256], int.MaxValue, int.MaxValue));
    }

    [TestMethod]
    public void FramebufferCopy_PaddedPitch_CopiesOnlyRectangleAndKeepsPadding()
    {
        var source = new int[4 * 3];
        for (int index = 0; index < source.Length; index++) source[index] = index + 1;
        // Zeilenlaenge 6 bei 4 sichtbaren Pixeln: die Polsterpixel duerfen nie beschrieben werden.
        var target = new int[6 * 3];
        Array.Fill(target, -9);
        var copied = FramebufferCopy.CopyRectangle(source, 4, 3, target, 6, 4, 3, 1, 1, 2, 2);
        Assert.AreEqual((1, 1, 2, 2), copied);
        CollectionAssert.AreEqual(new[] { -9, -9, -9, -9, -9, -9, -9, 6, 7, -9, -9, -9, -9, 10, 11, -9, -9, -9 }, target);
    }

    [TestMethod]
    public void FramebufferCopy_RectanglesAtEdges_AreClipped_AndCoverMatchesFullCopy()
    {
        var source = new int[4 * 3];
        for (int index = 0; index < source.Length; index++) source[index] = index + 1;
        var partial = new int[5 * 3];
        Assert.AreEqual((0, 0, 2, 1), FramebufferCopy.CopyRectangle(source, 4, 3, partial, 5, 4, 3, -1, -2, 3, 3));
        Assert.AreEqual((2, 1, 2, 2), FramebufferCopy.CopyRectangle(source, 4, 3, partial, 5, 4, 3, 2, 1, 9, 16));
        Assert.AreEqual(0, FramebufferCopy.CopyRectangle(source, 4, 3, partial, 5, 4, 3, int.MaxValue, int.MinValue, 9, 16).Width);
        CollectionAssert.AreEqual(new[] { 1, 2, 0, 0, 0, 0, 0, 7, 8, 0, 0, 0, 11, 12, 0 }, partial);
        // Wie Canvas.Display(): ein kleinerer Framebuffer begrenzt den kopierten Bereich.
        var small = new int[3 * 2];
        Assert.AreEqual((0, 0, 3, 2), FramebufferCopy.CopyRectangle(source, 4, 3, small, 3, 3, 2, 0, 0, 4, 3));
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 5, 6, 7 }, small);
    }
}
