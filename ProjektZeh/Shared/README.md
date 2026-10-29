# Gemeinsame Ergebnistypen

Die ZehOS-Bausteine verwenden `ResultErr`, `ResultObject<T>` und `ResultCode` aus dem Namespace `ProjektZeh.Shared`. Das Testprojekt bindet dieselben Quelldateien ein.

`ResultErr` beschreibt Erfolg oder Fehler einer Operation. `ResultObject<T>` enthält einen solchen Status und einen optionalen Wert. Beide Typen sind unveränderlich. Ein Wert, der weitergereicht werden soll, entsteht über `CreateSuccess`; Fehler entstehen über `CreateFailure` oder `CreateFromException`.

```csharp
using ProjektZeh.Shared;

ResultErr completed = ResultErr.CreateSuccess();
ResultErr failed = ResultErr.CreateFailure(ResultCode.InvalidArgument, "Ungueltige Eingabe.");

ResultObject<string> text = ResultObjectFactory.CreateSuccess<string>("Ergebnis");
ResultObject<string> missing = ResultObjectFactory.CreateFailure<string>(ResultCode.NotFound, "Nicht gefunden.");

if (text.IsOK())
{
    string? content = text.Value;
}

ResultObject<int> propagated = ResultObjectFactory.CreateFailure<int>(failed);
```

Ein noch nicht gesetztes Ergebnis meldet `Uninitialized` und keinen Erfolg. Nur `Success` erfüllt `IsOK()`. Fehler liefern den Standardwert von `T`; bei Referenztypen ist das `null`. Ein erfolgreiches Ergebnis darf ebenfalls `null` enthalten, wenn die jeweilige Operation das erlaubt. Deshalb entscheidet der Status über Erfolg, nicht der Wert.

`Status.Code` und `Status.Message` liefern die Ursache eines fehlgeschlagenen Wertergebnisses. Exceptions werden mit ihrer tatsächlichen Meldung und vollständigen Diagnose unter `ExceptionDetails` erfasst. Fehlerstatus lassen sich unverändert weiterreichen. Die allgemeinen Fehlercodes decken ungültige oder fehlende Argumente, nicht gefundene Inhalte, allgemeine Fehler und Exceptions ab.

Die Implementierung ist für diese Anforderungen in ZehOS neu geschrieben. Sie verwendet keine Paketabhängigkeit und keine übernommenen API-Fehlermodelle oder fachlichen Fehlerkataloge aus dem Backend. Die öffentliche API ist auf die ZehOS-Verwendung zugeschnitten.
