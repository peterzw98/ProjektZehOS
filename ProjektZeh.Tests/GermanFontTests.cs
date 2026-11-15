using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Graphics;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class GermanFontTests
{
    [DataTestMethod]
    [DataRow('ä', 132, 228)] [DataRow('ö', 148, 246)] [DataRow('ü', 129, 252)]
    [DataRow('Ä', 142, 132)] [DataRow('Ö', 153, 150)] [DataRow('Ü', 154, 156)]
    [DataRow('ß', 225, 159)] [DataRow('€', 353, 164)] [DataRow('§', 21, 167)]
    [DataRow('°', 248, 176)] [DataRow('²', 253, 178)] [DataRow('³', 6, 179)]
    [DataRow('µ', 230, 181)] [DataRow('´', 8, 39)]
    public void DefaultFont_GermanGlyphsAreCopiedFromOriginalWithoutChangingText(char character, int glyph, int resourceGlyph)
    {
        var source = new byte[512 * 16];
        for (int i = 0; i < source.Length; i++) source[i] = (byte)(i / 16 % 251);
        byte[] normalized = GermanFont.Normalize(source, 8, 16);
        int destination = GermanFont.Glyph(character);
        CollectionAssert.AreEqual(source.AsSpan(glyph * 16, 16).ToArray(), normalized.AsSpan(destination * 16, 16).ToArray());
        Assert.AreEqual((byte)(glyph % 251), source[glyph * 16]);
        Assert.AreEqual((byte)'A', normalized['A' * 16]);
        Assert.AreEqual((char)destination, GermanFont.ConsoleText(character.ToString())[0]);
        var resource = new byte[256 * 64];
        for (int i = 0; i < resource.Length; i++) resource[i] = (byte)(i / 64);
        byte[] normalResource = GermanFont.Normalize(resource, 16, 32);
        CollectionAssert.AreEqual(resource.AsSpan(resourceGlyph * 64, 64).ToArray(), normalResource.AsSpan(destination * 64, 64).ToArray());
    }

    [TestMethod]
    public void PixelSurface_EuroUsesNormalizedFontSlot()
    {
        var font = new byte[256];
        font[128] = 0x80;
        int[] pixels = new int[8];
        new PixelSurface(8, 1, pixels).DrawText("€", 0, 0, 9, font, 8, 1);
        Assert.AreEqual(9, pixels[0]);
    }
}
