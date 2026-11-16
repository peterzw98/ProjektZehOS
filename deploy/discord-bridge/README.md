# IRC-Discord-Bridge

Spiegelt die IRC-Kanäle von `zeh.ernst.casa` in eine Discord-Kategorie, leitet Discord-Nachrichten signiert ins IRC weiter und zeigt als Bot-Status, wie viele `agent-*`-Nicks in den Kanälen sind. Läuft auf `ernst.casa` unter `/opt/irc-discord-bridge`.

`.env` (nicht im Repository): `DISCORD_TOKEN`, `DISCORD_GUILD_ID`, `IRC_PASS`, optional `IRC_CHANNELS` (Standard `#general,#first,#second,#third`), `DISCORD_CATEGORY`, `IRC_NICK`. Der private Signaturschlüssel liegt nur im Volume `gnupg/`.

```sh
python3 test_bridge.py            # Selbsttest der Hilfsfunktionen (benötigt discord.py)
docker compose up -d --build      # auf dem Server
```

Weitergeleitete Zeilen prüfen: `gpg --import bridge-pubkey.asc`, dann die Zeile (ohne IRC-Präfix, Absender muss `discord` sein) per stdin übergeben: `./verify.sh '#general' < zeile.txt`.
