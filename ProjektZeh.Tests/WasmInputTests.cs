using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Input;
using ProjektZeh.Runtime.Wasm;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class WasmInputTests
{
    [TestMethod]
    public void LegacyKeysAndNewEventsHaveIndependentConsumers()
    {
        var input = new WasmInputBuffer();
        input.Handle(new(InputEventType.KeyDown, 30));
        input.Handle(new(InputEventType.KeyDown, 30, IsRepeat: true));
        input.Handle(new(InputEventType.KeyUp, 30));
        Assert.AreEqual(new WasmInputEvent(WasmInputEventType.KeyDown, 30), Read(input));
        Assert.AreEqual(new WasmInputEvent(WasmInputEventType.KeyDown, 30), Read(input));
        Assert.AreEqual(new WasmInputEvent(WasmInputEventType.KeyUp, 30), Read(input));
        Assert.AreEqual(0x100 | 30, input.PollKey());
        Assert.AreEqual(30, input.PollKey());
        Assert.AreEqual(-1, input.PollKey());
        Assert.IsFalse(input.TryRead(out _));
    }

    [TestMethod]
    public void UnicodePairsAndInvalidSequencesBecomeCodePoints()
    {
        var input = new WasmInputBuffer();
        foreach (char character in "ä\ud83d\ude80\ud800x\udfff")
            input.Handle(new(InputEventType.Text, Character: character));
        foreach (int point in new[] { 0xe4, 0x1f680, 0xfffd, 'x', 0xfffd })
            Assert.AreEqual(new WasmInputEvent(WasmInputEventType.Text, point), Read(input));
        Assert.IsFalse(input.TryRead(out _));
    }

    [TestMethod]
    public void ResetDropsStaleEventsAndPartialTextAndReleasesLegacyKey()
    {
        var input = new WasmInputBuffer();
        input.Handle(new(InputEventType.KeyDown, 42));
        input.Handle(new(InputEventType.Text, Character: '\ud83d'));
        input.Handle(new(InputEventType.Reset));
        Assert.AreEqual(new WasmInputEvent(WasmInputEventType.Reset), Read(input));
        Assert.IsFalse(input.TryRead(out _));
        Assert.AreEqual(0x100 | 42, input.PollKey());
        Assert.AreEqual(42, input.PollKey());
        Assert.AreEqual(-1, input.PollKey());
        input.Handle(new(InputEventType.Text, Character: '\ude80'));
        Assert.AreEqual(0xfffd, Read(input).Code);
    }

    [TestMethod]
    public void FullLegacyQueueKeepsReleaseOfReportedKeys()
    {
        var input = new WasmInputBuffer();
        input.Handle(new(InputEventType.KeyDown, 1));
        for (int index = 0; index < 63; index++)
        {
            input.Handle(new(InputEventType.KeyDown, 2));
            input.Handle(new(InputEventType.KeyUp, 2));
        }
        input.Handle(new(InputEventType.KeyDown, 3));
        input.Handle(new(InputEventType.KeyUp, 1));
        input.Handle(new(InputEventType.KeyUp, 3));
        input.Handle(new(InputEventType.Reset));
        var codes = new List<int>();
        for (int code = input.PollKey(); code >= 0; code = input.PollKey())
            codes.Add(code);
        Assert.AreEqual(0x100 | 1, codes[0]);
        Assert.AreEqual(1, codes[^1]);
        Assert.AreEqual(codes.Count(code => code == (0x100 | 2)), codes.Count(code => code == 2));
        Assert.IsFalse(codes.Contains(0x100 | 3) || codes.Contains(3));
    }

    [TestMethod]
    public void OverflowSignalsResetAndRetainsLatestInput()
    {
        var input = new WasmInputBuffer();
        for (int index = 0; index < 129; index++)
            input.Handle(new(InputEventType.Text, Character: 'a'));
        Assert.AreEqual(new WasmInputEvent(WasmInputEventType.Reset), Read(input));
        Assert.AreEqual(new WasmInputEvent(WasmInputEventType.Text, 'a'), Read(input));
        Assert.IsFalse(input.TryRead(out _));
    }

    [TestMethod]
    public void PointerUsesDisplayedScaleAndLetterboxAndKeepsRelease()
    {
        var input = new WasmInputBuffer();
        input.SetViewport(1280, 1000, 640, 480);
        input.Handle(new(InputEventType.PointerDown, X: 100, Y: 120, Button: PointerButtons.Left));
        Assert.AreEqual(new WasmInputEvent(WasmInputEventType.PointerDown, 1, 50, 50), Read(input));
        input.Handle(new(InputEventType.PointerDown, X: 100, Y: 0, Button: PointerButtons.Right));
        input.Handle(new(InputEventType.PointerScroll, X: 100, Y: 0, ScrollDelta: 1));
        Assert.IsFalse(input.TryRead(out _));
        input.Handle(new(InputEventType.PointerUp, X: int.MaxValue, Y: int.MinValue, Button: PointerButtons.Left));
        Assert.AreEqual(new WasmInputEvent(WasmInputEventType.PointerUp, 1, 639, 0), Read(input));
    }

    [TestMethod]
    public void ConsecutiveMotionCoalescesWithoutReorderingTextOrWheel()
    {
        var input = new WasmInputBuffer();
        input.SetViewport(640, 480, 640, 480);
        input.Handle(new(InputEventType.PointerMove, X: 10, Y: 20));
        input.Handle(new(InputEventType.PointerMove, X: 30, Y: 40));
        input.Handle(new(InputEventType.Text, Character: 'a'));
        input.Handle(new(InputEventType.PointerMove, X: 50, Y: 60));
        input.Handle(new(InputEventType.PointerScroll, X: 50, Y: 60, ScrollDelta: -2));
        Assert.AreEqual(new WasmInputEvent(WasmInputEventType.PointerMove, 0, 30, 40), Read(input));
        Assert.AreEqual(new WasmInputEvent(WasmInputEventType.Text, 'a'), Read(input));
        Assert.AreEqual(new WasmInputEvent(WasmInputEventType.PointerMove, 0, 50, 60), Read(input));
        Assert.AreEqual(new WasmInputEvent(WasmInputEventType.Scroll, -2, 50, 60), Read(input));
        Assert.IsFalse(input.TryRead(out _));
    }

    [TestMethod]
    public void CroppedFrameAndResizeUseCurrentGeometry()
    {
        var input = new WasmInputBuffer();
        input.SetViewport(320, 200, 640, 480);
        input.Handle(new(InputEventType.PointerDown, X: 0, Y: 0, Button: PointerButtons.Middle));
        Assert.AreEqual(new WasmInputEvent(WasmInputEventType.PointerDown, 4, 160, 140), Read(input));
        input.SetViewport(320, 200, 160, 100);
        input.Handle(new(InputEventType.PointerDown, X: 318, Y: 198, Button: PointerButtons.Left));
        Assert.AreEqual(new WasmInputEvent(WasmInputEventType.PointerDown, 1, 159, 99), Read(input));
    }

    private static WasmInputEvent Read(WasmInputBuffer input)
    {
        Assert.IsTrue(input.TryRead(out WasmInputEvent value));
        return value;
    }
}
