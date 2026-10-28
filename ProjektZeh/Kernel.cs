using System;
using ProjektZeh.Shell;
using Sys = Cosmos.Kernel.System;

namespace ProjektZeh;

public sealed class Kernel : Sys.Kernel
{
    private readonly ShellService shell = new();
    private readonly ConsoleTextOutput output = new();

    protected override void BeforeRun()
    {
        output.WriteLine("ZehOS Shell - 'help' zeigt die verfuegbaren Befehle.");
    }

    protected override void Run()
    {
        Console.Write("zehos> ");
        var result = shell.ExecuteLine(Console.ReadLine(), output);
        if (!result.IsOK())
        {
            output.WriteLine(result.sErrMsg);
        }
    }
}
