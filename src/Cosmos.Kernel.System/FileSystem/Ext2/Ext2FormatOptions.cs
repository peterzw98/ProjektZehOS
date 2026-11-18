// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

namespace Cosmos.Kernel.System.FileSystem.Ext2;

/// <summary>
/// Parameters for <see cref="Ext2FileSystemType.TryFormat(global::System.ReadOnlySpan{char}, IVfsFormatOptions)"/>.
/// </summary>
public sealed class Ext2FormatOptions : IVfsFormatOptions
{
    /// <summary>Block size in bytes (2048 or 4096; 1024 is refused until the driver handles first_data_block).</summary>
    public uint BlockSize { get; set; } = 4096;

    /// <summary>Volume label, up to 16 characters.</summary>
    public string VolumeLabel { get; set; } = "";
}
