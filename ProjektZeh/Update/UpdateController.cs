using System;
using System.IO;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Update;

/// <summary>Haelt einen geprueften Stand an derselben Quelle fest; ein Quellenwechsel verwirft ihn.</summary>
internal sealed class UpdateController(UpdateVersion currentVersion, Func<UpdateEndpoint, IUpdateSource> createSource,
    IUpdateTarget target, string root = "/data")
{
    private readonly FileService files = new(root);
    private UpdateService? service;
    private string? activeSource;
    private bool restartRequired;
    private string ConfigPath => System.IO.Path.Combine(root, ".zehos-update.conf");
    public string CurrentVersion => currentVersion.ToString();

    public ResultObject<string> GetSource()
    {
        var endpoint = GetEndpoint();
        return endpoint.IsOK() ? ResultObjectFactory.CreateSuccess(endpoint.Value!.ToString()) : ResultObjectFactory.CreateFailure<string>(endpoint.Status);
    }

    private ResultObject<UpdateEndpoint> GetEndpoint()
    {
        var config = files.ReadText(ConfigPath, true);
        if (!config.IsOK()) return ResultObjectFactory.CreateFailure<UpdateEndpoint>(config.Status);
        string text = config.Value == null ? UpdateEndpoint.DefaultSource : config.Value.TrimEnd('\r', '\n');
        return UpdateEndpoint.TryParse(text, out var endpoint)
            ? ResultObjectFactory.CreateSuccess(endpoint)
            : ResultObjectFactory.CreateFailure<UpdateEndpoint>(ResultCode.InvalidArgument, "Ungueltige Updatequelle in " + ConfigPath);
    }

    public ResultErr SetSource(string text)
    {
        if (!UpdateEndpoint.TryParse(text, out var endpoint))
            return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Updatequelle muss host[:port] sein (Port 1–65535).");
        var saved = files.WriteText(ConfigPath, endpoint + "\n");
        if (saved.IsOK()) { service = null; activeSource = null; }
        return saved;
    }

    public ResultObject<UpdateManifest> Check()
    {
        if (restartRequired) return ResultObjectFactory.CreateFailure<UpdateManifest>(ResultCode.Failure, "Die Kernelersetzung wurde bereits versucht. Zuerst neu starten.");
        var ready = Prepare();
        return ready.IsOK() ? service!.Check() : ResultObjectFactory.CreateFailure<UpdateManifest>(ready);
    }

    public ResultErr Install(byte[]? kernel = null)
    {
        if (restartRequired) return ResultErr.CreateFailure(ResultCode.Failure, "Die Kernelersetzung wurde bereits versucht. Zuerst neu starten.");
        var ready = Prepare();
        if (!ready.IsOK()) return ready;
        var result = service!.Install(kernel);
        // Nach Beginn des Dateiaustauschs kann auch ein Bereinigungsfehler einen bereits ersetzten Kernel melden.
        if (service.RequiresRestart) restartRequired = true;
        return result;
    }

    public ResultErr InstallOffline(string file)
    {
        if (restartRequired) return ResultErr.CreateFailure(ResultCode.Failure, "Die Kernelersetzung wurde bereits versucht. Zuerst neu starten.");
        try
        {
            string path = Path.GetFullPath(file, root);
            if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Offline-Kernel muss unter " + root + " liegen.");
            var source = new FileUpdateSource(Path.GetDirectoryName(path)!);
            var offline = new UpdateService(source, target, currentVersion);
            var check = offline.Check();
            if (!check.IsOK()) return check.Status;
            var kernel = source.Read(Path.GetFileName(path), (int)check.Value!.Size);
            if (!kernel.IsOK()) return kernel.Status;
            var result = offline.Install(kernel.Value!);
            if (offline.RequiresRestart) restartRequired = true;
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return ResultErr.CreateFromException(exception); }
    }

    private ResultErr Prepare()
    {
        var source = GetEndpoint();
        if (!source.IsOK()) { service = null; activeSource = null; return source.Status; }
        if (service == null || !string.Equals(activeSource, source.Value!.ToString(), StringComparison.Ordinal))
        {
            service = new UpdateService(createSource(source.Value!), target, currentVersion);
            activeSource = source.Value!.ToString();
        }
        return ResultErr.CreateSuccess();
    }
}
