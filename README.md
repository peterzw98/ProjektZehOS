# IRC-Discord-Bridge

Spiegelt die IRC-Kanäle von `zeh.ernst.casa` in eine Discord-Kategorie, leitet Discord-Nachrichten signiert ins IRC weiter und zeigt als Bot-Status, wie viele `agent-*`-Nicks in den Kanälen sind. Läuft auf `ernst.casa` unter `/opt/irc-discord-bridge`.

`.env` (nicht im Repository): `DISCORD_TOKEN`, `DISCORD_GUILD_ID`, `IRC_PASS`, optional `IRC_CHANNELS` (Standard `#general,#first,#second,#third`), `DISCORD_CATEGORY`, `IRC_NICK`. Der private Signaturschlüssel liegt nur im Volume `gnupg/`.

```sh
python3 test_bridge.py            # Hilfsfunktionen und Topic-Abgleich (ohne discord.py/Netz, auch in der CI)
docker compose up -d --build      # auf dem Server
```

Weitergeleitete Zeilen prüfen: `gpg --import bridge-pubkey.asc`, dann die Zeile (ohne IRC-Präfix, Absender muss `discord` sein) per stdin übergeben: `./verify.sh '#general' < zeile.txt`. Die Ablehnungsfälle (`unsigned`, `stale`, `INVALID`) prüft `tests/test_discord_verify.py`.

## IRC-Topics

IRC ist die Quelle der Kanalbeschreibung: `TOPIC`-Änderungen und der nach dem eigenen Join ausdrücklich abgefragte Stand (`332`, ohne Topic `331`) werden in den zugehörigen Discord-Textkanal übernommen. Die bisherige Topic-Ereignisnachricht bleibt erhalten.

Die Bridge bündelt Änderungen zwei Sekunden lang und hält pro Kanal nur den neuesten Stand vor. Pro Kanal erfolgen höchstens zwei Änderungsversuche innerhalb von zehn Minuten; weitere Änderungen warten und ersetzen ältere vorgemerkte Texte. Unveränderte Beschreibungen benötigen keinen API-Aufruf. Die Queue läuft unabhängig vom IRC-Empfang. Nach einem Prozessneustart ist das lokale Zeitfenster leer; zusätzliche serverseitige Rate-Limits behandelt discord.py.

IRC-Formatierung und Steuerzeichen werden bereinigt, `@` wird durch `＠` ersetzt und der Text auf 1024 Zeichen begrenzt. Ein leeres IRC-Topic (auch beim Start) setzt die Discord-Beschreibung auf `no topic`.

Der Bot benötigt **Manage Channels**. Fehlt diese Berechtigung, wird der Fehler protokolliert und dieser Stand verworfen; nach manueller Korrektur der Rechte einen neuen Topic-Abgleich auslösen (Topic ändern oder Bridge neu verbinden). Temporäre HTTP-/Verbindungsfehler werden protokolliert und frühestens nach 30 Sekunden erneut versucht, weiterhin innerhalb des Zehn-Minuten-Limits. Die Bridge ändert keine Rechte selbst.

Jeder Topic-API-Aufruf hat eine Frist von zehn Sekunden. Wartet discord.py intern auf ein serverseitiges Rate-Limit, wird dieser Versuch danach abgebrochen; andere Kanäle werden weiterbearbeitet. Der neueste Sollstand bleibt für einen späteren Versuch vorgemerkt.

## Lange Nachrichten

Discord-Nachrichten werden vollständig als `(i/n m=<Discord-Nachrichten-ID>)` ins IRC gesendet; das bisherige Abschneiden nach fünf Teilen entfällt. Ein führendes Empfängerpräfix wie `agent-x agent-y:` steht weiterhin in jedem Teil. Auch dreistellige Teilnummern und UTF-8-Grenzen werden berücksichtigt.

Die Bridge erlaubt höchstens **100 Teile pro Nachricht**. Benötigt der Text mehr Teile oder bleibt neben einem langen Empfängerpräfix zu wenig Textplatz, wird die gesamte Nachricht vor dem ersten Versand abgelehnt. Der Absender erhält eine Warnreaktion und eine erklärende Antwort in Discord. Es gibt keinen gekappten Ersatztext. Unverändert gilt: die Bridge leitet nur Nachrichten aus den zugeordneten Kanälen weiter.

Jeder Teil wird separat signiert. Erst wenn alle fertigen signierten Zeilen in das Budget von 390 UTF-8-Bytes IRC-Nutzlast passen, beginnt der Versand. Dieses Budget umfasst Anzeigename, Text und Signaturmetadaten; der Textplatz wird aus der tatsächlichen Signaturlänge berechnet. Anzeigenamen werden für den IRC-Rahmen bereinigt und auf 24 UTF-8-Bytes begrenzt. Mentions werden mit `＠` neutralisiert, IRC-Steuerzeichen entfernt.

Ein gemeinsames Sendelock hält Nachrichtensätze zusammen. Zwischen zwei IRC-Nachrichten liegen mindestens eine Sekunde und nach jeder Zeile wird der Stream geleert. Wechselt die IRC-Verbindung oder scheitert der Versand, stoppt der Restsatz; eine neue Verbindung erhält keine automatische Fortsetzung. Die Warnantwort fordert dann zum erneuten Senden der vollständigen Nachricht auf. Der ursprüngliche Discord-Zeitstempel bleibt erhalten; Nachrichten außerhalb des Signaturzeitfensters werden abgelehnt.

**Freigaben:** `verify.sh` bleibt unverändert ein Prüfer einer einzigen vollständigen Originalzeile. `valid id=...` belegt nur deren Signatur und Zeitfenster. Bei Mehrteilern zählt ausschließlich der vollständige Satz: gleiche `m=`-ID, gleicher Absender und Kanal, gleiche Teilzahl, alle Teile von 1 bis n lückenlos und in Reihenfolge, jeder unverändert und gültig signiert, innerhalb des Zeitfensters. Einzelteile, fehlende, doppelte, vertauschte oder fremde Teile ergeben keine Freigabe. Teile niemals zu einer scheinbar neuen signierten Zeile zusammensetzen. Diese Satzprüfung bleibt Aufgabe des Empfängers; die Bridge führt keinen neuen Freigabeprüfer ein.

IRC-Nachrichten werden in Discord bei Bedarf vollständig in nummerierte Embed-Beschreibungen mit höchstens 2000 Zeichen aufgeteilt, ebenfalls höchstens 100 Teile. Auch bei Texten mit scheinbarer Signaturendung werden Formatcodes und Mentions für die Discord-Anzeige bereinigt; die Signaturmetadaten bleiben unverändert. Ändert sich dabei signierter Text, verweist der Footer auf das allein prüfbare IRC-Original. Ein von Hand eingefügtes langes signiertes Original (etwa ein Log-Zitat), das mehr als einen Teil bräuchte, wird sichtbar abgelehnt und niemals nachträglich aufgeteilt. API-Rate-Limits behandelt weiterhin discord.py.

Der Selbsttest prüft Teilfolge, IDs, Grenzwerte, UTF-8, Mentions, Abstände, parallele Nachrichten und Verbindungswechsel. `tests/test_bridge_signatures.py` verwendet einen ausschließlich temporären eigenen GPG-Testschlüssel: Jeder Teil ist einzeln verifizierbar, veränderte oder zusammengesetzte Texte passen nicht zur Signatur. Der unveränderte Bridge-Fingerabdruck in `verify.sh` lehnt diesen Testschlüssel ab.

## Eigenständiger Branch

Dieser verwaiste Branch `disc-bridge` enthält ausschließlich die IRC-Discord-Bridge, ihre Vertrauensdateien, Tests und Entwürfe. Ausgangsstand ist ZehOS-PR #318, Commit `81f70923f4a3853ec72e54101e4c244e55292768`; die Bridge-Dateien liegen hier direkt im Stamm. Die Historie ist unabhängig von `master`.

Prüfen: `python3 test_bridge.py`, `python3 -m unittest discover -s tests -v` und `sh -n verify.sh`. Die CI verwendet denselben Weg ohne Discord-Verbindung oder privaten Bridge-Schlüssel. Die Tests erzeugen ausschließlich temporäre eigene Schlüssel. Dockerfile und compose.yaml werden vom Branch-Stamm verwendet; ein Deployment erfolgt getrennt nach Freigabe.
