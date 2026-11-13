using System;
using System.Globalization;
using System.Text;
using ProjektZeh.Shared;

namespace ProjektZeh.Update;

internal sealed record UpdateManifest(UpdateVersion Version, string Kernel, long Size, string Sha256)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ResultObject<UpdateManifest> Parse(byte[] bytes)
    {
        try
        {
            string text = StrictUtf8.GetString(bytes);
            string[] lines = text.Split('\n');
            if (lines.Length != 6 || lines[5].Length != 0 || lines[0] != "zehos-update 1" ||
                !lines[1].StartsWith("version=", StringComparison.Ordinal) ||
                !lines[2].StartsWith("kernel=", StringComparison.Ordinal) ||
                !lines[3].StartsWith("size=", StringComparison.Ordinal) ||
                !lines[4].StartsWith("sha256=", StringComparison.Ordinal)) return Invalid();
            string versionText = lines[1][8..];
            string kernel = lines[2][7..];
            string sizeText = lines[3][5..];
            string hash = lines[4][7..];
            if (!UpdateVersion.TryParse(versionText, out var version) ||
                kernel.Length == 0 || kernel == "." || kernel == ".." ||
                !ValidFileName(kernel) || sizeText.Length == 0 || sizeText[0] == '0' ||
                !Digits(sizeText) || !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out long size) || size <= 0 ||
                hash.Length != 64 || !IsHex(hash)) return Invalid();
            return ResultObjectFactory.CreateSuccess(new UpdateManifest(version, kernel, size, hash));
        }
        catch (DecoderFallbackException)
        {
            return Invalid();
        }
    }

    public byte[] Serialize() => StrictUtf8.GetBytes($"zehos-update 1\nversion={Version}\nkernel={Kernel}\nsize={Size.ToString(CultureInfo.InvariantCulture)}\nsha256={Sha256}\n");

    private static bool ValidFileName(string name)
    {
        foreach (char c in name)
            if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c is not ('.' or '-' or '_')) return false;
        return true;
    }

    private static bool Digits(string text)
    {
        foreach (char c in text) if (c < '0' || c > '9') return false;
        return true;
    }

    private static bool IsHex(string text)
    {
        foreach (char c in text)
            if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f') && !(c >= 'A' && c <= 'F')) return false;
        return true;
    }

    private static ResultObject<UpdateManifest> Invalid() => ResultObjectFactory.CreateFailure<UpdateManifest>(ResultCode.InvalidArgument, "Ungueltiges Update-Manifest.");
}
