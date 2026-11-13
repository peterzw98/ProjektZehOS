using System;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;

namespace ProjektZeh.Shell;

internal sealed class TaskShellCommands
{
    private readonly BackgroundTaskService tasks;
    private readonly Func<string[]> scheduler;
    public ShellCommand[] Commands { get; }
    public TaskShellCommands(BackgroundTaskService tasks, Func<string[]> scheduler)
    {
        this.tasks = tasks;
        this.scheduler = scheduler;
        Commands = [new("tasks", "tasks [list|start busy|start wait|cancel <ID>|scheduler]", "Hintergrundaufgaben und Round-Robin-Diagnose.", true, Execute)];
    }
    private ResultErr Execute(string[] arguments, ITextOutput output)
    {
        if (arguments.Length == 0 || (arguments.Length == 1 && arguments[0] == "list"))
        {
            foreach (var task in tasks.Snapshot())
                output.WriteLine($"{task.Id} {task.Name} {task.State} progress={task.Progress} {task.Error}");
            return ResultErr.CreateSuccess();
        }
        if (arguments.Length == 1 && arguments[0] == "scheduler")
        {
            foreach (var entry in scheduler()) output.WriteLine(entry);
            return ResultErr.CreateSuccess();
        }
        if (arguments.Length == 2 && arguments[0] == "cancel" && int.TryParse(arguments[1], out int id))
            return tasks.Cancel(id);
        if (arguments.Length == 2 && arguments[0] == "start" && arguments[1] is "busy" or "wait")
        {
            var started = tasks.Start(arguments[1], arguments[1] == "busy" ? Busy : Wait);
            if (!started.IsOK()) return started.Status;
            output.WriteLine($"Aufgabe {started.Value} gestartet.");
            return ResultErr.CreateSuccess();
        }
        return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Verwendung: tasks [list|start busy|start wait|cancel <ID>|scheduler]");
    }
    private static void Busy(BackgroundTaskContext context)
    {
        long progress = 0;
        while (!context.Token.IsCancellationRequested)
        {
            progress++;
            if ((progress & 4095) == 0) context.Report(progress);
        }
        context.Report(progress);
    }
    private static void Wait(BackgroundTaskContext context)
    {
        long progress = 0;
        while (true) { context.Sleep(250); context.Report(++progress); }
    }
}
