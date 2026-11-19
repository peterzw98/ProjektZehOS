// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using Cosmos.Kernel.System.Network;
using Cosmos.Kernel.System.Network.Protocols.Tcp;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.Network.Protocols.Tcp;

public class TcpConnectionTest
{
    private TcpConnection _target = null!;

    [SetUp]
    public void Setup()
    {
        _target = TcpConnection.CreateConnection(0, 0, new Address4(1, 2, 3, 4), new Address4(1, 2, 3, 4));
    }

    [TestFixture]
    public class AppendToData : TcpConnectionTest
    {
        [Test]
        public void WhenBothData_AndOtherAreEmpty_DataIsEmpty()
        {
            _target.AppendToData([]);

            Assert.That(_target.Data.Length, Is.EqualTo(0));
        }

        [Test]
        public void WhenDataIsNotEmpty_AndOtherIsEmpty_DataDoesNotChange()
        {
            _target.AppendToData([0, 1]);

            _target.AppendToData([]);

            Assert.That(_target.Data.ToArray(), Is.EquivalentTo([0, 1]));
        }

        [Test]
        public void WhenDataIsNotEmpty_AndOtherIsNotEmpty_OtherIsAppendedToData()
        {
            _target.AppendToData([0, 1]);

            _target.AppendToData([2, 3]);

            Assert.That(_target.Data.ToArray(), Is.EquivalentTo([0, 1, 2, 3]));
        }

        [Test]
        public void WhenPartOfDataWasRead_AndOtherIsAppended_TheUnreadDataIsKept()
        {
            _target.AppendToData([0, 1, 2, 3]);
            _target.AdvanceDataOffset(3);

            _target.AppendToData([4, 5]);

            Assert.That(_target.Data.ToArray(), Is.EqualTo(new byte[] { 3, 4, 5 }));
        }

        [Test]
        public void WhenPartOfDataWasRead_AndOtherOutgrowsTheBuffer_TheUnreadDataIsKept()
        {
            _target.AppendToData([0, 1, 2, 3]);
            _target.AdvanceDataOffset(3);

            byte[] other = Enumerable.Range(4, 100).Select(i => (byte)i).ToArray();
            _target.AppendToData(other);

            byte[] expected = [3, .. other];
            Assert.That(_target.Data.ToArray(), Is.EqualTo(expected));
        }

        [Test]
        public void WhenAllDataWasRead_AndOtherIsAppended_OnlyOtherIsKept()
        {
            _target.AppendToData([0, 1, 2]);
            _target.AdvanceDataOffset(3);

            _target.AppendToData([3, 4]);

            Assert.That(_target.Data.ToArray(), Is.EqualTo(new byte[] { 3, 4 }));
        }
    }

    [TestFixture]
    public class AdvanceDataOffset : TcpConnectionTest
    {
        [Test]
        public void WhenAdvancingByZero_NoChangesAreMade()
        {
            _target.AppendToData([0, 1]);

            _target.AdvanceDataOffset(0);

            Assert.That(_target.Data.ToArray(), Is.EquivalentTo([0, 1]));
        }

        [Test]
        public void WhenAdvancingByOneAndLengthIsTwo_OnlyLastElementRemains()
        {
            _target.AppendToData([0, 1]);

            _target.AdvanceDataOffset(1);

            Assert.That(_target.Data.ToArray(), Is.EquivalentTo([1]));
        }
        [Test]
        public void WhenAdvancingByTwoAndLengthIsTwo_DataLengthIsZero()
        {
            _target.AppendToData([0, 1]);

            _target.AdvanceDataOffset(2);

            Assert.That(_target.Data.Length, Is.Zero);
        }
    }

    // ZehOS patch tcp-receive-window: das angebotene Fenster folgt dem freien Puffer.
    [TestFixture]
    public class ReceiveWindow : TcpConnectionTest
    {
        [Test]
        public void WhileTheBufferHasRoom_TheWindowIsTheFullWindowSize()
        {
            _target.AppendToData(new byte[1000]);

            Assert.That(_target.TCB.RcvWnd, Is.EqualTo((uint)TcpConnection.TcpWindowSize));
        }

        [Test]
        public void NearTheLimit_TheWindowIsTheFreeRoom()
        {
            _target.AppendToData(new byte[TcpConnection.ReceiveBufferLimit - 1000]);

            Assert.That(_target.TCB.RcvWnd, Is.EqualTo(1000u));
        }

        [Test]
        public void AtTheLimit_TheWindowIsZero_AndReadingOpensIt()
        {
            _target.AppendToData(new byte[TcpConnection.ReceiveBufferLimit]);
            Assert.That(_target.TCB.RcvWnd, Is.Zero);

            _target.AdvanceDataOffset(TcpConnection.TcpWindowSize / 2);

            Assert.That(_target.TCB.RcvWnd, Is.EqualTo((uint)(TcpConnection.TcpWindowSize / 2)));
        }

        [Test]
        public void ReadDataOpensTheWindow_AndKeepsTheBytes()
        {
            byte[] sent = Enumerable.Range(0, TcpConnection.ReceiveBufferLimit).Select(i => (byte)i).ToArray();
            _target.AppendToData(sent);

            byte[] read = new byte[TcpConnection.TcpWindowSize];
            int count = _target.ReadData(read);

            Assert.That(count, Is.EqualTo(read.Length));
            Assert.That(read, Is.EqualTo(sent.AsSpan(0, read.Length).ToArray()));
            Assert.That(_target.TCB.RcvWnd, Is.EqualTo((uint)TcpConnection.TcpWindowSize));
        }
    }
}
