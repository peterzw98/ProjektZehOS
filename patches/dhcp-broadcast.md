# dhcp-broadcast

- **Commit:** Squash dieses PRs (Refs #719)
- **Dateien:** `src/Cosmos.Kernel.System/Network/Protocols/IPv4/Dhcp/DhcpPacket.cs`, `tests/Cosmos.Kernel.Tests.System/Network/Protocols/Dhcp/DhcpPacketTest.cs`
- **Grund:** ZehOS #719, unter VMware Workstation (e1000e, NAT) liefert `net dhcp` keine Adresse. `DhcpPacket` schrieb das BOOTP-Feld `flags` als 0; ohne BROADCAST-Bit schickt der ISC-basierte `vmnet-dhcpd` das OFFER per Unicast an die angebotene Adresse (RFC 2131 §4.1). `IPv4Handler` nimmt vor der Konfiguration nur Pakete an Adressen aus `NetworkStack.AddressMap` oder Broadcast an und verwirft das OFFER (`[IP] Packet not for us, dropping`). QEMU user-net antwortet immer per Broadcast, deshalb fiel es dort nicht auf. Jetzt setzt der Konstruktor für DISCOVER und REQUEST (`DhcpPacket(MacAddress, ushort)`) das BROADCAST-Bit (`RawData[52] = 0x80`); RELEASE nutzt den anderen Konstruktor und lässt `flags` bei 0, wie RFC 2131 Tabelle 5 verlangt.
- **Offen, nicht in diesem Patch:** `IPv4Handler` nimmt weiterhin kein Unicast an Port 68 vor der Konfiguration an; nur für Server nötig, die das Bit ignorieren.
- **Entfernter ZehOS-Umweg:** keiner
- **Prüfung:** Hosttests `DhcpPacketTest` (neu; DISCOVER und REQUEST ohne Fix rot), `Cosmos.Kernel.Tests.System` gesamt; Cosmos-Bau und ZehOS-Gastläufe siehe ZehOS-PR
