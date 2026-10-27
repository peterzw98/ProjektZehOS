using System;
using Sys = Cosmos.Kernel.System;

namespace ProjektZeh;

public sealed class Kernel : Sys.Kernel
{
    protected override void BeforeRun()
    {
        Console.WriteLine("ProjektZeh booted with Cosmos Gen3. Type a line of text to echo it.");
    }

    protected override void Run()
    {
        Console.Write("Input: ");
        string? input = Console.ReadLine();
        Console.Write("Text typed: ");
        Console.WriteLine(input);
    }
}
