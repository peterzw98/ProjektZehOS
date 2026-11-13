using System;
using Org.BouncyCastle.Crypto.Digests;
using ProjektZeh.Shared;

namespace ProjektZeh.Update;

internal interface IUpdateSource
{
    ResultObject<byte[]> Read(string name);
}

internal interface IUpdateTarget
{
    ResultErr ReplaceKernel(byte[] kernel);
    ResultErr WriteVersion(string version);
}

internal sealed class UpdateService(IUpdateSource source, IUpdateTarget target, UpdateVersion currentVersion)
{
    private UpdateManifest? checkedManifest;
    public bool RequiresRestart { get; private set; }

    public ResultObject<UpdateManifest> Check()
    {
        checkedManifest = null;
        if (RequiresRestart) return ResultObjectFactory.CreateFailure<UpdateManifest>(ResultCode.Failure, "Zuerst neu starten.");
        var manifestBytes = source.Read("update-manifest.txt");
        if (!manifestBytes.IsOK()) return ResultObjectFactory.CreateFailure<UpdateManifest>(manifestBytes.Status);
        var signature = source.Read("update-manifest.sig");
        if (!signature.IsOK()) return ResultObjectFactory.CreateFailure<UpdateManifest>(signature.Status);
        if (!ManifestSignature.Verify(manifestBytes.Value!, signature.Value!))
            return ResultObjectFactory.CreateFailure<UpdateManifest>(ResultCode.Failure, "Ungueltige Manifest-Signatur.");
        var parsed = UpdateManifest.Parse(manifestBytes.Value!);
        if (!parsed.IsOK()) return parsed;
        if (parsed.Value!.Version.CompareTo(currentVersion) <= 0)
            return ResultObjectFactory.CreateFailure<UpdateManifest>(ResultCode.Failure, "Das Update ist nicht neuer als der laufende Kernel.");
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
            var downloaded = source.Read(checkedManifest.Kernel);
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
