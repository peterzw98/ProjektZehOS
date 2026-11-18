using Microsoft.VisualStudio.TestTools.UnitTesting;

// Klassen laufen parallel (Workers = 0: so viele wie Prozessoren); Tests einer Klasse weiterhin nacheinander.
// Klassen mit gemeinsamem statischem Zustand des Produktcodes (Wasm-Laufzeit, Theme, Uhr, StorageLock, Kennwort-
// Worker, Netz-Connector, TLS-Cache) oder mit Zeitmessungen tragen [DoNotParallelize]; sie laufen danach allein.
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.ClassLevel)]
