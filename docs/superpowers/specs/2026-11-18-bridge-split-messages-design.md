# Vollständige Bridge-Nachrichten in nummerierten Teilen

## Auftrag und Ansatz
Lange Discord-Nachrichten werden bisher nach fünf IRC-Teilen gekappt. Beauftragt ist vollständiges Senden als `(i/n m=<Discord-ID>)` sowie nummerierte Discord-Ausgabe bis 2000 Zeichen je Teil. Vorhandene Helfer und asyncio genügen; keine neue Bibliothek. Direkte ungebremste Sends wären Flood-anfällig, eine persistente Queue ist für diesen Auftrag unnötig.

Discord -> IRC: maximal 100 Teile, darüber die ganze Nachricht sichtbar ablehnen; gleiche Nick-Präfixe in jedem Teil, UTF-8-Bytegrenzen und dreistellige Teilnummern einrechnen. Erst den vollständigen Satz signieren und seine Transportgrößen prüfen, dann unter einem Sendelock mit mindestens einer Sekunde Abstand und drain nach jeder Zeile senden. Der IRC-Empfang und PING bleiben unabhängig. Bereits signierte Zeilen nie erneut zerlegen. Bei Verbindungswechsel keinen Restsatz an eine neue Verbindung senden.

Die bestehende Signatur über Kanal, Benutzer-ID, Zeit und den unveränderten Teiltext einschließlich Nummer/Message-ID bleibt. verify.sh bleibt ein Einzeilenprüfer ohne neuen Modus; eine gültige Teil-Signatur ist keine Freigabe für den ganzen Satz. Der Empfänger muss weiterhin alle n Teile desselben Satzes in Reihenfolge, unverändert, gültig signiert und innerhalb des Zeitfensters besitzen. Sender-Tests prüfen diese Protokollinvarianten und negative Varianten; keine automatische Empfangsfreigabe wird eingeführt.

IRC -> Discord: normale Nachrichten vollständig in höchstens 2000 Zeichen langen, nummerierten Embed-Beschreibungen darstellen; Mentions neutralisieren. Eine zu große bereits signierte Zeile wird abgelehnt, nicht nachträglich zerlegt. Höchstens 100 Teile; übergroße Eingaben sichtbar melden. Discords vorhandener Client bleibt für API-Limits zuständig.

Kein Deploy, kein Merge, keine Schlüssel-/Rechteänderung. Architektur und Signatur-/Transportlogik beim Hauptagenten; begrenzter Regressionstest lokal delegiert.
