using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Input;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class InputTests
{
    [TestMethod]
    public void Queue_Full_DoesNotOverwriteWaitingEvents()
    {
        var queue = new InputEventQueue(2);
        Assert.IsTrue(queue.TryEnqueue(new(InputEventType.KeyDown, 30)));
        Assert.IsTrue(queue.TryEnqueue(new(InputEventType.KeyUp, 30)));
        Assert.IsFalse(queue.TryEnqueue(new(InputEventType.KeyDown, 31)));
        Assert.IsTrue(queue.TryDequeue(out var first));
        Assert.AreEqual(InputEventType.KeyDown, first.Type);
        Assert.IsTrue(queue.TryEnqueue(new(InputEventType.Text, Character: 'x')));
        Assert.IsTrue(queue.TryDequeue(out var second));
        Assert.AreEqual(InputEventType.KeyUp, second.Type);
        Assert.IsTrue(queue.TryDequeue(out var third));
        Assert.AreEqual('x', third.Character);
        Assert.IsFalse(queue.TryDequeue(out _));
    }

    [TestMethod]
    public void Keys_PressRepeatRelease_KeepTextSeparateAndClearState()
    {
        var input = new InputService(100, 80);
        input.ReportKey(30, false, 'a');
        input.ReportKey(30, false, 'a');
        Assert.IsTrue(input.IsKeyDown(30));
        input.ReportKey(30, true);
        Assert.IsFalse(input.IsKeyDown(30));
        Assert.AreEqual(0, input.PressedKeyCount);
        Assert.IsTrue(input.TryRead(out var down));
        Assert.AreEqual(InputEventType.KeyDown, down.Type);
        Assert.IsFalse(down.IsRepeat);
        Assert.AreEqual('\0', down.Character);
        Assert.IsTrue(input.TryRead(out var text));
        Assert.AreEqual(InputEventType.Text, text.Type);
        Assert.AreEqual('a', text.Character);
        Assert.IsTrue(input.TryRead(out var repeat));
        Assert.IsTrue(repeat.IsRepeat);
        Assert.IsTrue(input.TryRead(out _));
        Assert.IsTrue(input.TryRead(out var up));
        Assert.AreEqual(InputEventType.KeyUp, up.Type);
        Assert.IsFalse(input.TryRead(out _));
    }

    [TestMethod]
    public void Pointer_OutsideScreen_ClampsAndReportsBothButtonEdges()
    {
        var input = new InputService(100, 80);
        input.ReportPointer(-50, 500, PointerButtons.Left | PointerButtons.Right);
        Assert.AreEqual(0, input.PointerX);
        Assert.AreEqual(79, input.PointerY);
        Assert.IsTrue(input.TryRead(out var move));
        Assert.AreEqual(InputEventType.PointerMove, move.Type);
        Assert.IsTrue(input.TryRead(out var left));
        Assert.AreEqual(PointerButtons.Left, left.Button);
        Assert.AreEqual(InputEventType.PointerDown, left.Type);
        Assert.IsTrue(input.TryRead(out var right));
        Assert.AreEqual(PointerButtons.Right, right.Button);
        input.ReportPointer(0, 79, PointerButtons.None);
        Assert.IsTrue(input.TryRead(out var leftUp));
        Assert.AreEqual(InputEventType.PointerUp, leftUp.Type);
        Assert.IsTrue(input.TryRead(out _));
        Assert.IsFalse(input.TryRead(out _));
    }

    [TestMethod]
    public void Input_Overflow_ResetsHeldKeysAndNotifiesConsumer()
    {
        var input = new InputService(100, 80);
        input.ReportKey(30, false);
        for (var i = 0; i < InputService.QueueCapacity; i++)
        {
            input.ReportKey(31, false);
        }
        Assert.AreEqual(0, input.PressedKeyCount);
        Assert.IsFalse(input.IsKeyDown(30));
        Assert.IsTrue(input.DroppedEvents > 0);
        Assert.IsTrue(input.TryRead(out var reset));
        Assert.AreEqual(InputEventType.Reset, reset.Type);
    }
}
