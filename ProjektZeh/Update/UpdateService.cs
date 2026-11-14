using System;
using Org.BouncyCastle.Crypto.Digests;
using ProjektZeh.Shared;

namespace ProjektZeh.Update;

internal interface IUpdateSource
{
    /// <summary>Liest hoechstens maxBytes; groessere Antworten gelten als Fehler.</summary>
    ResultObject<byte[]> Read(string name, int maxBytes);
}

internal interface IUpdateTarget
{
    /// <summary>rotateBackup=false laesst die vorhandene .bak unveraendert.</summary>
    ResultErr ReplaceKernel(byte[] kernel, bool rotateBackup);
    ResultErr WriteVersion(string version);
    /// <summary>Installierte Version laut Versionsmarker; Erfolg ohne Wert, wenn kein Marker existiert.</summary>
    ResultObject<string> ReadVersion();
}

internal sealed class UpdateService(IUpdateSource source, IUpdateTarget target, UpdateVersion currentVersion)
{
    private UpdateManifest? checkedManifest;
    private bool runningFromFallback;
    private string? previousMarker;
    public bool RequiresRestart { get; private set; }

    public ResultObject<UpdateManifest> Check()
    {
        checkedManifest = null;
        if (RequiresRestart) return ResultObjectFactory.CreateFailure<UpdateManifest>(ResultCode.Failure, "Zuerst neu starten.");
        var manifestBytes = source.Read("update-manifest.txt", UpdateManifest.MaximumManifestBytes);
        if (!manifestBytes.IsOK()) return ResultObjectFactory.CreateFailure<UpdateManifest>(manifestBytes.Status);
        var signature = source.Read("update-manifest.sig", ManifestSignature.SignatureBytes);
        if (!signature.IsOK()) return ResultObjectFactory.CreateFailure<UpdateManifest>(signature.Status);
        if (!ManifestSignature.Verify(manifestBytes.Value!, signature.Value!))
            return ResultObjectFactory.CreateFailure<UpdateManifest>(ResultCode.Failure, "Ungueltige Manifest-Signatur.");
        var parsed = UpdateManifest.Parse(manifestBytes.Value!);
        if (!parsed.IsOK()) return parsed;
        if (parsed.Value!.Version.CompareTo(currentVersion) <= 0)
            return ResultObjectFactory.CreateFailure<UpdateManifest>(ResultCode.Failure, "Das Update ist nicht neuer als der laufende Kernel.");
        // Nach einem Start ueber den Rueckfalleintrag laeuft der Kernel aus .bak: nur der defekte Hauptkernel wird ersetzt,
        // und das Update muss auch neuer als die installierte, defekte Version sein.
        var installed = target.ReadVersion();
        if (!installed.IsOK()) return ResultObjectFactory.CreateFailure<UpdateManifest>(installed.Status);
        bool fallback = installed.Value != null && !string.Equals(installed.Value.Trim(), currentVersion.ToString(), StringComparison.Ordinal);
        if (fallback)
        {
            if (!UpdateVersion.TryParse(installed.Value!.Trim(), out var installedVersion))
                return ResultObjectFactory.CreateFailure<UpdateManifest>(ResultCode.Failure, "Der Versionsmarker /boot/zehos-version.txt ist ungueltig.");
            if (parsed.Value.Version.CompareTo(installedVersion) <= 0)
                return ResultObjectFactory.CreateFailure<UpdateManifest>(ResultCode.Failure,
                    $"Rueckfallstart: Das Update ist nicht neuer als die installierte Version {installedVersion}.");
        }
        runningFromFallback = fallback;
        previousMarker = installed.Value?.Trim();
        checkedManifest = parsed.Value;
        return parsed;
    }

    public ResultErr Install(byte[]? localKernel = null)
    {
        if (RequiresRestart) return ResultErr.CreateFailure(ResultCode.Failure, "Vor einem weiteren Update neu starten.");
        if (checkedManifest == null) return ResultErr.CreateFailure(ResultCode.Failure, "Zuerst update check ausfuehren.");
        byte[] kernel;
        if (localKernel == null)
        {
            var downloaded = source.Read(checkedManifest.Kernel, (int)checkedManifest.Size);
            if (!downloaded.IsOK()) return downloaded.Status;
            kernel = downloaded.Value!;
        }
        else kernel = localKernel;
        if (kernel.LongLength != checkedManifest.Size)
            return ResultErr.CreateFailure(ResultCode.Failure, "Die Kernelgroesse stimmt nicht mit dem Manifest ueberein.");
        var digest = new Sha256Digest();
        digest.BlockUpdate(kernel, 0, kernel.Length);
        byte[] hash = new byte[digest.GetDigestSize()];
        digest.DoFinal(hash, 0);
        if (!string.Equals(Convert.ToHexString(hash), checkedManifest.Sha256, StringComparison.OrdinalIgnoreCase))
            return ResultErr.CreateFailure(ResultCode.Failure, "Die Kernelpruefsumme stimmt nicht mit dem Manifest ueberein.");
        // Marker zuerst: Bleibt er nach einem Fehler falsch stehen, gilt der naechste Start als Rueckfallstart
        // und laesst .bak unangetastet. Die umgekehrte Reihenfolge koennte einen Rueckfallstart verdecken.
        ResultErr marked = target.WriteVersion(checkedManifest.Version.ToString());
        if (!marked.IsOK()) return marked;
        RequiresRestart = true;
        checkedManifest = null;
        ResultErr replaced = target.ReplaceKernel(kernel, rotateBackup: !runningFromFallback);
        if (replaced.IsOK()) return replaced;
        ResultErr restored = target.WriteVersion(previousMarker ?? currentVersion.ToString());
        return restored.IsOK() ? replaced : ResultErr.CreateFailure(ResultCode.Failure,
            $"{replaced.Message} Der Versionsmarker konnte nicht zurueckgesetzt werden: {restored.Message}");
    }
}
