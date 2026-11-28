namespace ProjektZeh.Processes.Vfs;

/// <summary>
/// Linux-Fehlernummern (x86-64, asm-generic/errno-base.h und errno.h), die das Dateisystem der Linux-Prozesse liefert.
/// Rückgaben sind immer der negative Wert, wie beim Verteiler <c>LinuxSyscalls.Dispatch</c>.
/// </summary>
internal static class LinuxErrno
{
    public const long EPERM = 1, ENOENT = 2, EIO = 5, EBADF = 9, EACCES = 13, EBUSY = 16, EEXIST = 17, EXDEV = 18,
        ENOTDIR = 20, EISDIR = 21, EINVAL = 22, EMFILE = 24, EFBIG = 27, ENOSPC = 28, ESPIPE = 29, EROFS = 30,
        ENAMETOOLONG = 36, ENOTEMPTY = 39, ELOOP = 40, EOVERFLOW = 75, EOPNOTSUPP = 95;
}

/// <summary>Bits von <c>st_mode</c> und Typen von <c>d_type</c>, soweit das Dateisystem sie liefert.</summary>
internal static class LinuxMode
{
    public const uint TypeMask = 0xF000, Regular = 0x8000, Directory = 0x4000, Symlink = 0xA000, CharDevice = 0x2000;
    public const uint PermissionMask = 0x1FF; // 0777
    public const uint OwnerRead = 0x100, OwnerWrite = 0x80, OwnerExecute = 0x40;

    public const byte DtChar = 2, DtDirectory = 4, DtRegular = 8, DtSymlink = 10;
}
