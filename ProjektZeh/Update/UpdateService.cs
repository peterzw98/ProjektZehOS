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
    ResultErr ReplaceKernel(byte[] kernel);
    ResultErr WriteVersion(string version);
    /// <summary>Installierte Version laut Versionsmarker; Erfolg ohne Wert, wenn kein Marker existiert.</summary>
    ResultObject<string> ReadVersion();
}

internal sealed class UpdateService(IUpdateSource source, IUpdateTarget target, UpdateVersion currentVersion)
{
    private UpdateManifest? checkedManifest;
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
        // Nach einem Start ueber den Rueckfalleintrag wuerde ein Update den einzigen funktionierenden Kernel aus .bak verdraengen.
        var installed = target.ReadVersion();
        if (!installed.IsOK()) return ResultObjectFactory.CreateFailure<UpdateManifest>(installed.Status);
        if (installed.Value != null && !string.Equals(installed.Value.Trim(), currentVersion.ToString(), StringComparison.Ordinal))
            return ResultObjectFactory.CreateFailure<UpdateManifest>(ResultCode.Failure,
                $"Laufender Kernel {currentVersion} ist nicht die installierte Version {installed.Value.Trim()} (Rueckfalleintrag?). " +
                "Updates sind erst nach Start der installierten Version oder einer Neuinstallation moeglich.");
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
        RequiresRestart = true;
        ResultErr replaced = target.ReplaceKernel(kernel);
        if (!replaced.IsOK()) return replaced;
        string installedVersion = checkedManifest.Version.ToString();
        checkedManifest = null;
        return target.WriteVersion(installedVersion);
    }
}
