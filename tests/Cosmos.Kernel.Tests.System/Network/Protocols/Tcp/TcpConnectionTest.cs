// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System.Reflection;
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

    // Baut Segmente ueber die experimentelle Paketnaht (TcpPacket, TcpFlags).
#pragma warning disable COSMOS0002
    // ZehOS patch tcp-receive-window: der Empfangsweg ueber ReceiveData, also
    // Fensterpruefung und ProcessEstablished. Die Gegenstelle ist eine Adresse,
    // zu der der Host keine Schnittstelle hat; die ACKs, die der Weg sendet,
    // werden deshalb nicht eingereiht, und der Test sieht nur den Zustand.
    [TestFixture]
    public class ReceivePath : TcpConnectionTest
    {
        private const uint PeerSequence = 1000;
        private const uint OwnSequence = 500;

        [SetUp]
        public void Established()
        {
            _target.Status = Status.ESTABLISHED;
            _target.TCB.RcvNxt = PeerSequence;
            _target.TCB.SndUna = OwnSequence;
            _target.TCB.SndNxt = OwnSequence;
            _target.TCB.RcvWnd = TcpConnection.TcpWindowSize;
        }

        // Ein Datensegment der Gegenstelle, wie der Empfangsweg es parst.
        private TcpPacket Segment(uint sequenceNumber, int length)
        {
            byte[] data = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
            TcpPacket built = new(_target.RemoteEndPoint.Address, _target.LocalEndPoint.Address,
                _target.RemoteEndPoint.Port, _target.LocalEndPoint.Port, sequenceNumber, OwnSequence, 20,
                (byte)(TcpFlags.ACK | TcpFlags.PSH), TcpConnection.TcpWindowSize, 0, data);
            return new TcpPacket(built.Network);
        }

        [Test]
        public void AnInOrderSegment_IsStored_AndRcvNxtMovesPastIt()
        {
            _target.ReceiveData(Segment(PeerSequence, 100));

            Assert.That(_target.DataLength, Is.EqualTo(100));
            Assert.That(_target.TCB.RcvNxt, Is.EqualTo(PeerSequence + 100));
        }

        // Steht fuer den Fall aus E5-O: Im Kernel liefert eine gescheiterte
        // Allokation null, und AppendToData wirft, bevor es Zustand aendert.
        // Auf dem Host scheitert keine Allokation; ein Puffer null wirft an
        // derselben Stelle (vor jeder Aenderung) und ist ohne Testnaht im Kernel.
        [Test]
        public void WhenAppendingThrows_RcvNxtStays_SoTheBytesAreNotAcknowledged()
        {
            typeof(TcpConnection).GetField("_data", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_target, null);

            Assert.Throws<NullReferenceException>(() => _target.ReceiveData(Segment(PeerSequence, 100)));

            Assert.That(_target.TCB.RcvNxt, Is.EqualTo(PeerSequence));
            Assert.That(_target.DataLength, Is.Zero);
        }

        [Test]
        public void ASegmentEndingAtTheWindowsEdge_IsTaken()
        {
            _target.AppendToData(new byte[TcpConnection.ReceiveBufferLimit - 1000]);
            Assert.That(_target.TCB.RcvWnd, Is.EqualTo(1000u));

            _target.ReceiveData(Segment(PeerSequence, 1000));

            Assert.That(_target.DataLength, Is.EqualTo(TcpConnection.ReceiveBufferLimit));
            Assert.That(_target.TCB.RcvNxt, Is.EqualTo(PeerSequence + 1000));
            Assert.That(_target.TCB.RcvWnd, Is.Zero);
        }

        // Die Grenze haelt auch ohne das Fenster: RcvWnd wird hier von Hand
        // weiter gesetzt, als der Puffer Platz hat. (Ein Segment hinter der
        // Fensterkante laesst sich auf dem Host nicht pruefen: der Abweisungszweig
        // protokolliert ueber Serial, das nur im Kernel laeuft.)
        [Test]
        public void ASegmentTheBufferHasNoRoomFor_IsNotTaken_EvenInsideTheWindow()
        {
            _target.AppendToData(new byte[TcpConnection.ReceiveBufferLimit - 100]);
            _target.TCB.RcvWnd = TcpConnection.TcpWindowSize;

            _target.ReceiveData(Segment(PeerSequence, 200));

            Assert.That(_target.DataLength, Is.EqualTo(TcpConnection.ReceiveBufferLimit - 100));
            Assert.That(_target.TCB.RcvNxt, Is.EqualTo(PeerSequence));
        }
    }
#pragma warning restore COSMOS0002
}
