# IRC-Discord-Bridge

Spiegelt die IRC-Kanäle von `zeh.ernst.casa` in eine Discord-Kategorie, leitet Discord-Nachrichten signiert ins IRC weiter und zeigt als Bot-Status, wie viele `agent-*`-Nicks in den Kanälen sind. Läuft auf `ernst.casa` unter `/opt/irc-discord-bridge`.

`.env` (nicht im Repository): `DISCORD_TOKEN`, `DISCORD_GUILD_ID`, `IRC_PASS`, optional `IRC_CHANNELS` (Standard `#general,#first,#second,#third`), `DISCORD_CATEGORY`, `IRC_NICK`. Der private Signaturschlüssel liegt nur im Volume `gnupg/`.

```sh
python3 test_bridge.py            # Hilfsfunktionen und Topic-Abgleich (ohne discord.py/Netz, auch in der CI)
docker compose up -d --build      # auf dem Server
```

Weitergeleitete Zeilen prüfen: `gpg --import bridge-pubkey.asc`, dann die Zeile (ohne IRC-Präfix, Absender muss `discord` sein) per stdin übergeben: `./verify.sh '#general' < zeile.txt`. Die Ablehnungsfälle (`unsigned`, `stale`, `INVALID`) prüft `scripts/tests/test_discord_verify.py`.

## IRC-Topics

IRC ist die Quelle der Kanalbeschreibung: `TOPIC`-Änderungen und der nach dem eigenen Join ausdrücklich abgefragte Stand (`332`, ohne Topic `331`) werden in den zugehörigen Discord-Textkanal übernommen. Die bisherige Topic-Ereignisnachricht bleibt erhalten.

Die Bridge bündelt Änderungen zwei Sekunden lang und hält pro Kanal nur den neuesten Stand vor. Pro Kanal erfolgen höchstens zwei Änderungsversuche innerhalb von zehn Minuten; weitere Änderungen warten und ersetzen ältere vorgemerkte Texte. Unveränderte Beschreibungen benötigen keinen API-Aufruf. Die Queue läuft unabhängig vom IRC-Empfang. Nach einem Prozessneustart ist das lokale Zeitfenster leer; zusätzliche serverseitige Rate-Limits behandelt discord.py.

IRC-Formatierung und Steuerzeichen werden bereinigt, `@` wird durch `＠` ersetzt und der Text auf 1024 Zeichen begrenzt. Ein leeres IRC-Topic leert auch die Discord-Beschreibung.

Der Bot benötigt **Manage Channels**. Fehlt diese Berechtigung, wird der Fehler protokolliert und dieser Stand verworfen; nach manueller Korrektur der Rechte einen neuen Topic-Abgleich auslösen (Topic ändern oder Bridge neu verbinden). Temporäre HTTP-/Verbindungsfehler werden protokolliert und frühestens nach 30 Sekunden erneut versucht, weiterhin innerhalb des Zehn-Minuten-Limits. Die Bridge ändert keine Rechte selbst.
