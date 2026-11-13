using System;
using System.Collections.Generic;

namespace ProjektZeh.Security;

[Flags]
internal enum FilePermission { None = 0, Read = 1, Write = 2 }

internal sealed class FilePermissions(string ownerId)
{
    private readonly Dictionary<string, FilePermission> grants = new(StringComparer.Ordinal);
    public string OwnerId { get; } = ownerId;
    public IEnumerable<KeyValuePair<string, FilePermission>> Grants => grants;

    public bool Allows(string userId, FilePermission permission) =>
        OwnerId == userId || (grants.TryGetValue(userId, out var actual) && (actual & permission) == permission);

    public void Grant(string userId, FilePermission permission)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        if ((permission & ~(FilePermission.Read | FilePermission.Write)) != 0)
            throw new ArgumentOutOfRangeException(nameof(permission));
        if (permission == FilePermission.None) grants.Remove(userId);
        else grants[userId] = permission;
    }
}
