using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class FileErrorsTests
{
    [TestMethod]
    public void IsFileError_AcceptsExpectedFileErrorsIncludingSubtypes()
    {
        Assert.IsTrue(FileErrors.IsFileError(new IOException()));
        Assert.IsTrue(FileErrors.IsFileError(new FileNotFoundException()));
        Assert.IsTrue(FileErrors.IsFileError(new UnauthorizedAccessException()));
        Assert.IsTrue(FileErrors.IsFileError(new ArgumentNullException("path")));
        Assert.IsTrue(FileErrors.IsFileError(new NotSupportedException()));
    }

    [TestMethod]
    public void IsFileError_LetsProgrammingErrorsThrough()
    {
        // Diese Fehler zeigen einen Programmfehler und duerfen nicht als Dateifehler verschluckt werden.
        Assert.IsFalse(FileErrors.IsFileError(new InvalidOperationException()));
        Assert.IsFalse(FileErrors.IsFileError(new FormatException()));
        Assert.IsFalse(FileErrors.IsFileError(new KeyNotFoundException()));
    }
}
