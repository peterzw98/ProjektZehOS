# IRC-Discord-Bridge

Spiegelt die IRC-Kanäle von `zeh.ernst.casa` in eine Discord-Kategorie und leitet Discord-Nachrichten signiert ins IRC weiter. Läuft auf `ernst.casa` unter `/opt/irc-discord-bridge`.

`.env` (nicht im Repository): `DISCORD_TOKEN`, `DISCORD_GUILD_ID`, `IRC_PASS`, optional `IRC_CHANNELS` (Standard `#general,#first,#second,#third`), `DISCORD_CATEGORY`, `IRC_NICK`. Der private Signaturschlüssel liegt nur im Volume `gnupg/`.

```sh
python3 test_bridge.py            # Selbsttest der Hilfsfunktionen (benötigt discord.py)
docker compose up -d --build      # auf dem Server
```

Weitergeleitete Zeilen prüfen: `gpg --import bridge-pubkey.asc`, dann `./verify.sh '#general' '<Name> text [id=… t=… sig=…]'`.
