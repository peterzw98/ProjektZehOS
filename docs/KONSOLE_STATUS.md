# Moderne Konsole: Arbeits- und Prüfstand

Stand 14. November 2026, Branch feature/moderne-konsole. Freigegebener Umfang: KDE-inspirierte Konsole, Standard-Datei-/Textbefehle, kein eigener Netzwerkausbau.

## Umsetzung
Tabs (maximal acht), rekursive horizontale/vertikale Teilungen (vier Ansichten pro Tab), getrennte Shells/Arbeitsverzeichnisse/Identitäten/Eingaben/Verläufe, Auswahl und interne Zwischenablage, Suche über weiche Umbrüche, Scrollleiste, Profile und Bitmapzoom. Kontextmenü und Tastaturbedienung; Größen und Klickbereiche berücksichtigen 8×16 und tatsächliche 16×32-Gastschrift. Identitätswechsel verwirft private Terminalzustände; mehrfaches Schließen gibt Sitzungen nur einmal frei.

Shell: Vervollständigung von Befehlen/Pfaden, Ctrl-R-Verlaufssuche, Argumentaliase, begrenzte Pipes/Umleitungen mit Rechteprüfung je Stufe, interaktiver nichtblockierender Pager. Befehle und unterstützte Optionen stehen vollständig in README.md. Kopieren/Verschieben verweigern bestehende Ziele, Löschen ist nicht rekursiv, bestehende Ersetzungs-/Wiederherstellungslogik bleibt erhalten. Ein spätes Scheitern der Rechtepersistenz wird als Fehler gemeldet und sperrt Rechte; Verschieben/Löschen kann zu diesem Zeitpunkt bereits erfolgt sein. Keine Stromausfallgarantie.

## Nachweise auf Ausgangsbasis aa35e79
- SDK 10.0.403, vorhandene Cosmos-Werkzeuge geprüft; keine Updates oder neuen Abhängigkeiten.
- Ausgangsbasis 277 Hosttests; fertiger Konsolenstand 330/330 mit Release, --no-restore und -warnaserror. Neue Tests zuerst mit beobachtetem Fehler, danach erfolgreich.
- Normaler nativer Release-Build erfolgreich. Separater dotnet-Recovery-Build erfolgreich mit null Warnungen/Fehlern; normale und Recovery-ISOs besitzen unterschiedliche Hashes und wurden getrennt gestartet.
- QEMU q35, CPU max,512 MiB, Standard-VGA,16×32-Gastschrift und1920×1080. Tests auf isolierter Kopie des Datenimages. Original-SHA256 vor/nach Prüfung unverändert:81BDA458C2B40BF139CED8B767C6B82BB11DFE264C27967C75DD139FDB523576.
- Normaler Boot, Testkonto-Ersteinrichtung, erneute Anmeldung nach Shutdown, Shell und Eingabe geprüft. date liefert Gastzeit; free echte physische Seitenwerte; df echte FAT32-Kapazität/freien Platz.
- Anlegen/Anhängen auch bei zuvor fehlender Datei unter echten ACLs; Kopieren/Verschieben/Touch; head/tail/find/du; Filterpipeline cat|grep|wc sowie sort|uniq im Gast ausgeführt. Keine Daten auf dem Originalimage verändert.
- Zwei Tabs und drei Ansichten mit beiden Teilungsrichtungen und unterschiedlichen Ausgaben; Tab-/Fokuswechsel, Profil Hell, Ausgabesuche mit sichtbarem Treffer,200-%-Zoom und help|less mit echter Leertaste geprüft. F4/Esc erhält die Ansichten; Textshell nimmt echo fallback-alive an. Eigene Test-VM über shutdown mit Aushängen beendet.
- Recovery startet textuell und nimmt nach Anmeldung Befehle an; Tab-Vervollständigung echo native-completion funktioniert. Pager/Alias/Pipeline zusätzlich geprüft; Gaststart und Screenshots bleiben lokale, ignorierte Prüfarbeitsdaten.
- VMware Player: getrennte plattenlose UEFI-VM startet; seriell Textshell ist bereit und Desktop ist bereit. captureScreen verweigert anonyme Gastoperationen. Keine Anmeldung, Terminalbedienung oder Dateioperationen in VMware nachgewiesen; eigene VM danach beendet.
- Unabhängiges Quellreview: fehlendes ACL-Append-Ziel, reale Pager-Leertaste und fontabhängige Schließen-Klickbereiche gefunden und mit Regressionstests korrigiert. Abschließende erneute unabhängige Prüfung ohne bestätigten Muss-Befund.

## Grenzen
Kein KDE-/GNU-/POSIX-Port und keine Linux-Binärausführung. Profile und Aliase sind sitzungslokal, Scrollback256 Anzeigezeilen, Pipelines höchstens16 Stufen und65536 Ausgabezeichen, Dateisuche1024 Einträge. wc -c zählt Zeichen; grep sucht festen Teiltext. Cosmos3.0.94 pflegt FAT-Zeitstempel nicht. Vollständige VMware-Bedienprüfung und echte Hardware bleiben offen.

## Integration
Nach der ursprünglichen Prüfung ist origin/master auf b51cb86 fortgeschritten (Installer/Updates, Mausoptimierung und Netzwerk). Der Konsolencommit wurde auf b51cb86 neu aufgesetzt; Überschneidungen in Kernel und Dokumentation erhalten beide Funktionsstände. Fremde Netzwerkfunktionen bleiben erhalten; sie sind kein Bestandteil dieses Konsolenauftrags.

### Integrierte Prüfung auf b51cb86
- Vollständiger Release-Hosttestlauf mit Warnungen als Fehler:375 bestanden,1 fehlgeschlagen,376 insgesamt. Fehlgeschlagen ist ausschließlich der übernommene `TlsTransportTests.TlsChecksChainNameAndDates`.
- Der TLS-Test wurde zweimal in einem isolierten Testprojekt mit unveränderten Masterquellen reproduziert. Sein Windows-Schannel-Testserver wirft beim ephemeren RSA-Schlüssel `AuthenticationException: platform does not support ephemeral keys`, innerer Fehler0x8009030E. Der Client empfängt deshalb handshake_failure(40). Der Konsolenbranch verändert weder TLS-Transport noch Zertifikatsprüfung oder Testfixture. Der vollständige Hoststand wird daher ausdrücklich nicht als vollständig grün bezeichnet; nachfolgende Fälle im kombinierten TLS-Test werden nicht erreicht. Keine Reparatur am ausgeschlossenen Netzwerkumfang vorgenommen.
- Integrierter nativer Normal-Build und erneuter Recovery-Build erfolgreich; Recovery null Warnungen/Fehler. Die Abbilder wurden separat gesichert. Neues USB-Tablet-Profil aus master bleibt erhalten.
- Erneutes unabhängiges Integrationsreview ohne bestätigten Muss-Befund: zusätzliche Installer-/Update-Dateimethoden und Netzwerkstatus/-abbruch sowie Sitzungsfreigabe und Energievorbereitung bleiben erhalten.
- Repository-Einstellung `delete_branch_on_merge` als true geprüft. Der Pull Request wird ohne Merge erstellt; Peter ist als Autor kein möglicher eigener Reviewer.
- Integrierter QEMU-Normalgast mit USB-Tablet erneut geprüft: Anmeldung, Terminalstart per absoluter Maus, Pipelineausgabe1, echte df-Werte, ACL-Append auf neue Datei und erneutes Lesen, Aliasaufruf, Tab/Teilansicht, exit schließt nur die Teilansicht. Sichtbares Such-x mit echter Maus angeklickt; danach nimmt die Shell echo mouse-close an. Normalgast über shutdown beendet. Integrierter Recovery-Gast startet eindeutig als Textshell mit Anmeldeprompt; die nur lesend gestartete isolierte Testinstanz danach beendet. Die weitergehenden Recovery-Bediennachweise auf aa35e79 stehen oben und werden nicht als neue Prüfung nach dem Rebase ausgegeben.

## Veröffentlichung

[PR35](https://github.com/peterzw98/ProjektZehOS/pull/35) ist offen gegen master. Lars (larsernst) und azazel (Azazel55605) sind als Reviewer angefordert; Peter ist Autor und Assignee. Der Branch ist gepusht und konfliktfrei. Automatische Prüfungen sind im PR einsehbar; kein Merge ausgeführt.
