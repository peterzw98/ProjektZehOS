using ProjektZeh.Messaging;
using ProjektZeh.Shared;

namespace ProjektZeh.Shell;

internal sealed class MessageShellCommands
{
    private readonly MessageDemoService demo;
    public ShellCommand[] Commands { get; }

    public MessageShellCommands(MessageDemoService demo)
    {
        this.demo = demo;
        Commands = [new("messages", "messages start slow-producer|slow-consumer|status|close|stop",
            "Zeigt begrenzte Nachrichten und feste Sperrreihenfolge zwischen zwei Aufgaben.", true, Execute)];
    }

    private ResultErr Execute(string[] arguments, ITextOutput output)
    {
        if (arguments.Length == 2 && arguments[0] == "start")
        {
            if (arguments[1] != "slow-producer" && arguments[1] != "slow-consumer") return InvalidArguments();
            var result = demo.Start(arguments[1] == "slow-producer");
            if (result.IsOK()) output.WriteLine("24 nummerierte Nachrichten, Puffer 3. Status: messages status. Sperren immer Konto 1, dann Konto 2.");
            return result;
        }
        if (arguments.Length != 1) return InvalidArguments();
        switch (arguments[0])
        {
            case "status": output.WriteLine(demo.Status()); break;
            case "close": demo.Close(); output.WriteLine("Puffer geschlossen; vorhandene Nachrichten werden noch verarbeitet."); break;
            case "stop": demo.Stop(); output.WriteLine("Abbruch angefordert."); break;
            default: return InvalidArguments();
        }
        return ResultErr.CreateSuccess();
    }

    private static ResultErr InvalidArguments() => ResultErr.CreateFailure(ResultCode.InvalidArgument,
        "Verwendung: messages start slow-producer|slow-consumer oder messages status|close|stop");
}
