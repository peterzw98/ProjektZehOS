// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using Cosmos.Kernel.HAL.Devices.Network;
using Cosmos.Kernel.System.Network;
using Cosmos.Kernel.System.Network.Protocols.IPv4.Dhcp;
using NUnit.Framework;

// These cases are the tests of the experimental packet seam, so they opt into
// it rather than the project doing so for every other fixture.
#pragma warning disable COSMOS0002

namespace Cosmos.Kernel.Tests.System.Network.Protocols.Dhcp;

// ZehOS patch dhcp-broadcast, see PATCHES.md. The BOOTP flags field sits at
// frame offsets 52-53. RFC 2131 table 5: DISCOVER and REQUEST may set the
// BROADCAST bit, RELEASE must leave flags at 0.
[TestFixture]
public class DhcpPacketTest
{
    private static readonly MacAddress s_mac = new([0x52, 0x54, 0x00, 0x12, 0x34, 0x56]);

    [Test]
    public void Discover_SetsTheBroadcastFlag()
    {
        DhcpDiscover packet = new(s_mac);

        Assert.That(packet.RawData[52..54], Is.EqualTo(new byte[] { 0x80, 0x00 }));
    }

    [Test]
    public void Request_SetsTheBroadcastFlag()
    {
        DhcpRequest packet = new(s_mac, new Address4(172, 16, 163, 128));

        Assert.That(packet.RawData[52..54], Is.EqualTo(new byte[] { 0x80, 0x00 }));
    }

    [Test]
    public void Release_LeavesTheFlagsZero()
    {
        DhcpRelease packet = new(new Address4(172, 16, 163, 128), new Address4(172, 16, 163, 254), s_mac);

        Assert.That(packet.RawData[52..54], Is.EqualTo(new byte[] { 0x00, 0x00 }));
    }
}
