# ext2-inode-cache

- **Commit:** Squash von #<PR>
- **Dateien:** `src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2Superblock.cs` (`_inodeCache`)
- **Grund:** Der Inode-Cache (`Dictionary`) wuchs mit jedem gelesenen Inode und wurde dabei umkopiert; ab rund 17.500 Inodes je Mount scheiterte die Vergrößerung an der Zerstückelung des Kernel-Heaps. Jetzt beim Mount auf `min(InodesCount, 65.536)` vorab dimensioniert (erste Stufe laut Plan; Verdrängung ist ein eigener Entwurf). Kosten: bis zu rund 2,0 MiB je Mount sofort statt nach Bedarf (Kapazität 75.431 nach Primzahlrundung); über rund 75.000 Inodes je Mount wächst das Wörterbuch wie bisher
- **Entfernter ZehOS-Umweg:** keiner; die dokumentierte Grenze (`README.md:174`, `docs/COSMOS_SUPPORT.md:174`, bisher rund 17.500) ändert der ZehOS-PR, der den Pin setzt, auf rund 75.000
- **Prüfung:** NUnit `Ext2InodeCacheTests` (1500 Inodes: Kapazität unverändert, ohne Patch rot; Deckel 65.536 als Rechenfall); Gastlauf E4.0 `--diagnose-run 12` offen
