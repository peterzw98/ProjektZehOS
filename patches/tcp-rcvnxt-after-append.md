# tcp-rcvnxt-after-append

- **Commit:** Squash des PRs gegen `cosmos`
- **Dateien:** `src/Cosmos.Kernel.System/Network/Protocols/Tcp/TcpConnection.cs`
- **Grund:** `ProcessEstablished` erhöhte `TCB.RcvNxt` vor `AppendToData`. Wirft das Anhängen (im ZehOS-Gast gesehen: `[NetworkManager] packet handler threw: Destination is too short`), fängt `KitNetworkConsumer.OnReceive` die Ausnahme; das Segment ist verloren, `RcvNxt` aber schon weiter, und das nächste ACK bestätigt die verlorenen Bytes. Die Gegenstelle wiederholt sie nie, der Datenstrom hat eine Lücke (bei TLS: `bad_record_mac`). Jetzt wird erst angehängt, dann `RcvNxt` erhöht. `AppendToData` ändert beim Wurf keinen Zustand (alle Zuweisungen nach den Kopien), das Segment bleibt also unbestätigt und kommt als Wiederholung ab `RcvNxt` erneut. Die Ursache des Wurfs selbst behebt der Patch nicht. ZehOS E5-O (#530).
- **Entfernter ZehOS-Umweg:** keiner
- **Prüfung:** Cosmos-Bau (`scripts/build-cosmos.sh`); ZehOS nativer Bau und Doom-WAD-Download über HTTPS (KVM, SMP 8c4t2) mit Mitschnitt: kein bestätigter Datenverlust nach einem Wurf
