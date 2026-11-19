using Microsoft.VisualStudio.TestTools.UnitTesting;

// Klassen laufen parallel (CI-4, Spec ci-laufzeiten); Tests mit gemeinsamem Zustand tragen [DoNotParallelize].
[assembly: Parallelize(Scope = ExecutionScope.ClassLevel)]
