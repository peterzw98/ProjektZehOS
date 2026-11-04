using System;
using ProjektZeh.Shared;

namespace ProjektZeh.Shell;

/// <summary>
/// Verbindet Befehlsbeschreibung, Argumentregel und Ausfuehrung.
/// </summary>
internal sealed class ShellCommand
{
    private readonly bool acceptsArguments;
    private readonly Func<string[], ITextOutput, ResultErr> execute;

    public string Name { get; }
    public string Usage { get; }
    public string Description { get; }

    public ShellCommand(string name, string usage, string description, bool acceptsArguments,
        Action<string[], ITextOutput> execute)
        : this(name, usage, description, acceptsArguments, (arguments, output) =>
        {
            execute(arguments, output);
            return ResultErr.CreateSuccess();
        })
    {
    }

    public ShellCommand(string name, string usage, string description, bool acceptsArguments,
        Func<string[], ITextOutput, ResultErr> execute)
    {
        Name = name;
        Usage = usage;
        Description = description;
        this.acceptsArguments = acceptsArguments;
        this.execute = execute;
    }

    /// <summary>
    /// Fuehrt einen Befehl mit geprueften Argumenten aus.
    /// </summary>
    /// <param name="arguments"></param>
    /// <param name="output"></param>
    /// <returns></returns>
    public ResultErr Execute(string[]? arguments, ITextOutput? output)
    {
        if (arguments is null)
        {
            return ResultErr.CreateFailure(ResultCode.MissingArgument,
                "cause='InvalidInput' message='Die Argumentliste fehlt.'");
        }

        if (output is null)
        {
            return ResultErr.CreateFailure(ResultCode.MissingArgument,
                "cause='InvalidInput' message='Das Ausgabeziel fehlt.'");
        }

        if (!acceptsArguments)
        {
            if (arguments.Length > 0)
            {
                return ResultErr.CreateFailure(ResultCode.InvalidArgument,
                    $"cause='InvalidArguments' message='Verwendung: {Usage}'");
            }
        }

        return execute(arguments, output);
    }
}
