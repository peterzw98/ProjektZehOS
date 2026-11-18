using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Migration;
using ProjektZeh.Storage.Virtual;

namespace ProjektZeh.Storage;

/// <summary>
/// Dateizugriff mit eigenem Arbeitsverzeichnis und gemeinsamen Fehlerergebnissen.
/// </summary>
internal sealed class FileService
{
    internal const int MaximumTextLength = 65536;
    // Produktgrenze 65.536 je ext2-Verzeichnis (Lead-Entscheid E2-P7, 19.11.2026), ein Platz Reserve fuer Rename wie zuvor.
    // Cosmos selbst begrenzt seit TryParseDirectory (cosmos #345) nur noch auf 4096 Bloecke/16 MiB; geprueft sind 20.000 im Gast.
    internal const int MaximumExt2DirectoryEntries = 65535;
    private readonly Action<string, string, bool> moveFile;
    private readonly Func<string, ResultObject<(long TotalBytes, long AvailableBytes)>>? diskSpace;
    private readonly Func<string, int?>? directoryEntryLimit;
    private readonly Func<IDisposable?>? tryBeginWrite;
    private readonly FileAccessControl? access;
    private readonly UserSession? session;
    // Ohne Rechteverwaltung (Kontendatei) prueft dieser Waechter Symlinks und Mountgrenzen; mit Rechteverwaltung deren eigener.
    private readonly Func<string, bool, ResultErr>? pathGuard;
    // Erzeuger der /proc- und /sys-Dateien (normalisierter Pfad -> Snapshot); der Kernel liefert ihn aus Cosmos-Werten.
    private readonly Func<string, ResultObject<byte[]>>? virtualSource;

    /// <summary>Grosse Lesepuffer (Bilder, App-Speicher bis 16 MiB) mit Null-Abwehr; Tests setzen eine Allokation, die null liefert.</summary>
    internal Func<int, byte[]?> AllocateBuffer { get; init; } = BufferAllocation.TryAllocate;
    // Typquelle fuer Kopier-/Verschiebequellen (Spec §0.8); im Kernel VfsManager.TryStat + VfsStat.IsRegularFile.
    private readonly Func<string, bool> isRegularFile;

    public string CurrentDirectory { get; private set; }

    public FileService(string initialDirectory, Action<string, string, bool>? moveFile = null,
        FileAccessControl? access = null, UserSession? session = null,
        Func<string, ResultObject<(long TotalBytes, long AvailableBytes)>>? diskSpace = null,
        Func<string, bool, ResultErr>? pathGuard = null, Func<string, int?>? directoryEntryLimit = null,
        Func<string, ResultObject<byte[]>>? virtualSource = null,
        Func<string, bool>? isRegularFile = null,
        Func<IDisposable?>? tryBeginWrite = null)
    {
        this.virtualSource = virtualSource;
        this.isRegularFile = isRegularFile ?? IsRegularHostFile;
        this.pathGuard = pathGuard ?? access?.PathGuard;
        CurrentDirectory = initialDirectory;
        this.moveFile = moveFile ?? File.Move;
        this.diskSpace = diskSpace;
        this.directoryEntryLimit = directoryEntryLimit;
        this.tryBeginWrite = tryBeginWrite ?? access?.TryBeginWrite;
        this.access = access;
        this.session = session;
    }

    /// <summary>
    /// Listet Dateien und Unterverzeichnisse.
    /// </summary>
    public ResultObject<string[]> ListEntries(string? path) => StorageLock.Run(() => ListEntriesStorageLocked(path));

    private ResultObject<string[]> ListEntriesStorageLocked(string? path)
    {
        var resolved = ResolvePath(path);
        if (!resolved.IsOK())
        {
            return ResultObjectFactory.CreateFailure<string[]>(resolved.Status);
        }

        try
        {
            AuthorizeStorageLocked(resolved.Value!, FilePermission.Read);
            string[] entries = Directory.GetFileSystemEntries(resolved.Value!);
            var visible = new List<string>();
            foreach (string entry in entries)
                if (Visible(entry)) visible.Add(entry);
            return ResultObjectFactory.CreateSuccess(visible.ToArray());
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<string[]>(exception);
        }
    }

    public ResultObject<FileEntry[]> ListDirectory(string? path) => StorageLock.Run(() => ListDirectoryStorageLocked(path));

    private ResultObject<FileEntry[]> ListDirectoryStorageLocked(string? path)
    {
        var listed = ListEntries(path);
        if (!listed.IsOK()) return ResultObjectFactory.CreateFailure<FileEntry[]>(listed.Status);
        try
        {
            var entries = new FileEntry[listed.Value!.Length];
            for (int index = 0; index < entries.Length; index++)
            {
                string name = listed.Value[index];
                entries[index] = new(name, (File.GetAttributes(name) & FileAttributes.Directory) != 0);
            }
            Array.Sort(entries, static (left, right) =>
            {
                int type = right.IsDirectory.CompareTo(left.IsDirectory);
                return type != 0 ? type : string.Compare(left.Path, right.Path, StringComparison.Ordinal);
            });
            return ResultObjectFactory.CreateSuccess(entries);
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<FileEntry[]>(exception);
        }
    }

    /// <summary>
    /// Liest eine Textdatei und schliesst sie anschliessend. Mit <paramref name="allowMissing"/> liefert eine fehlende
    /// Datei Erfolg ohne Wert; alle anderen Fehler, auch ein fehlendes Verzeichnis, bleiben Fehler.
    /// </summary>
    public ResultObject<string> ReadText(string? path, bool allowMissing = false, int maximumLength = MaximumTextLength) => StorageLock.Run(() => ReadTextStorageLocked(path, allowMissing, maximumLength));

    private ResultObject<string> ReadTextStorageLocked(string? path, bool allowMissing, int maximumLength)
    {
        var resolved = ResolvePath(path, creating: allowMissing);
        if (!resolved.IsOK())
        {
            return resolved;
        }

        try
        {
            if (allowMissing && access != null && !access.Contains(resolved.Value!))
            {
                if (session == null || !access.Check(session, resolved.Value!, FilePermission.Write, true).IsOK())
                    throw new UnauthorizedAccessException("Zugriff verweigert.");
                // Nur ein wirklich fehlendes Ziel darf ohne Rechtezuordnung als leer gelten.
                _ = File.GetAttributes(resolved.Value!);
                throw new UnauthorizedAccessException("Vorhandener Eintrag ohne Rechtezuordnung.");
            }
            AuthorizeStorageLocked(resolved.Value!, FilePermission.Read);
            // Streng und ohne BOM-Umschaltung: ungueltige Bytes waeren sonst U+FFFD und beim Zurueckschreiben verloren (#201).
            using var reader = new StreamReader(resolved.Value!, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            var text = ReadToEnd(reader, maximumLength);
            return text == null
                ? ResultObjectFactory.CreateFailure<string>(TextTooLarge(maximumLength))
                : ResultObjectFactory.CreateSuccess(text);
        }
        catch (FileNotFoundException) when (allowMissing)
        {
            return ResultObjectFactory.CreateSuccess<string>(null);
        }
        catch (DecoderFallbackException)
        {
            return ResultObjectFactory.CreateFailure<string>(ResultCode.InvalidArgument,
                "cause='NotUtf8' message='Die Datei ist kein gueltiges UTF-8 und wird nicht als Text gelesen oder veraendert.'");
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<string>(exception);
        }
    }

    /// <summary>Liest bis EOF (nicht nach Stream.Length: /proc-Dateien melden 0); null, wenn mehr als <paramref name="maximumLength"/> Zeichen kommen.</summary>
    internal static string? ReadToEnd(TextReader reader, int maximumLength)
    {
        var text = new StringBuilder();
        var chunk = new char[4096];
        int read;
        while ((read = reader.Read(chunk, 0, (int)Math.Min(chunk.Length, (long)maximumLength + 1 - text.Length))) > 0)
        {
            text.Append(chunk, 0, read);
            if (text.Length > maximumLength)
            {
                return null;
            }
        }

        return text.ToString();
    }

    public ResultObject<byte[]> ReadBytes(string? path, int maximumLength) => StorageLock.Run(() => ReadBytesStorageLocked(path, maximumLength));

    private ResultObject<byte[]> ReadBytesStorageLocked(string? path, int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        var resolved = ResolvePath(path);
        if (!resolved.IsOK()) return ResultObjectFactory.CreateFailure<byte[]>(resolved.Status);
        try
        {
            AuthorizeStorageLocked(resolved.Value!, FilePermission.Read);
            using var stream = new FileStream(resolved.Value!, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = stream.Length;
            if (length < 0 || length > maximumLength)
                return ResultObjectFactory.CreateFailure<byte[]>(ResultCode.InvalidArgument, "Die Bilddatei ist zu gross.");
            // Laenge 0 kann auch "unbekannt" heissen (virtuelle Dateien wie unter Linux): dann begrenzt bis EOF lesen.
            if (length == 0) return ReadUntilEnd(stream, maximumLength, AllocateBuffer);
            if (AllocateBuffer((int)length) is not { } data) return OutOfMemory<byte[]>();
            stream.ReadExactly(data);
            return ResultObjectFactory.CreateSuccess(data);
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<byte[]>(exception);
        }
    }

    /// <summary>
    /// Liest bis EOF; mehr als <paramref name="maximumLength"/> Bytes sind ein Fehler statt einer gekuerzten Datei. Gelesen wird in
    /// Bloecken und erst am Ende in einen Puffer passender Groesse kopiert; jede Anforderung laeuft ueber
    /// <paramref name="allocate"/>, damit ein ausgeschoepfter Kernel-Heap als OutOfMemory endet statt als Absturz.
    /// </summary>
    internal static ResultObject<byte[]> ReadUntilEnd(Stream stream, int maximumLength, Func<int, byte[]?>? allocate = null)
    {
        allocate ??= BufferAllocation.TryAllocate;
        const int BlockLength = 4096;
        var blocks = new List<byte[]>();
        long total = 0;
        while (true)
        {
            if (allocate(BlockLength) is not { } block) return OutOfMemory<byte[]>();
            int filled = 0, read;
            while (filled < block.Length
                   && (read = stream.Read(block, filled, (int)Math.Min(block.Length - filled, (long)maximumLength + 1 - total - filled))) > 0)
                filled += read;
            total += filled;
            if (total > maximumLength)
                return ResultObjectFactory.CreateFailure<byte[]>(ResultCode.InvalidArgument, "Die Bilddatei ist zu gross.");
            if (filled > 0) blocks.Add(block);
            if (filled < block.Length) break;
        }

        if (allocate((int)total) is not { } data) return OutOfMemory<byte[]>();
        int offset = 0;
        foreach (byte[] block in blocks)
        {
            int count = (int)Math.Min(block.Length, total - offset);
            block.AsSpan(0, count).CopyTo(data.AsSpan(offset));
            offset += count;
        }

        return ResultObjectFactory.CreateSuccess(data);
    }

    /// <summary>Gehoert der Pfad (nach Normalisierung gegen das Arbeitsverzeichnis) zu /proc, /sys oder /dev?</summary>
    public bool IsVirtualPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && VirtualTree.IsVirtualPath(VirtualTree.Normalize(path, CurrentDirectory));

    /// <summary>
    /// Liest eine virtuelle Datei direkt aus dem Erzeuger, nie ueber System.IO (Spec E5b/E6b §0.5): auch wenn unter Root eine
    /// gleichnamige Datei liegt, wird sie nicht gelesen. /dev/zero endet wie ReadText mit dem Grenzfehler.
    /// </summary>
    public ResultObject<string> ReadVirtual(string? path, int maximumLength = MaximumTextLength)
    {
        var node = VirtualNodeFor(path, write: false);
        if (!node.IsOK()) return ResultObjectFactory.CreateFailure<string>(node.Status);
        var bytes = ReadVirtualBytes(node.Value!, maximumLength);
        if (!bytes.IsOK()) return ResultObjectFactory.CreateFailure<string>(bytes.Status);
        return bytes.Value!.Length > maximumLength
            ? ResultObjectFactory.CreateFailure<string>(TextTooLarge(maximumLength))
            : ResultObjectFactory.CreateSuccess(Encoding.UTF8.GetString(bytes.Value));
    }

    /// <summary>Gemeinsame Weiche fuer Textleser (cat, Filter, Betrachter): virtuelle Pfade aus dem Erzeuger, sonst <see cref="ReadText"/>.</summary>
    public ResultObject<string> ReadTextOrVirtual(string? path) => IsVirtualPath(path) ? ReadVirtual(path) : ReadText(path);

    /// <summary>Kinder eines virtuellen Verzeichnisses als absolute Pfade, Verzeichnisse zuerst wie <see cref="ListDirectory"/>.</summary>
    public ResultObject<FileEntry[]> ListVirtual(string? path)
    {
        var node = VirtualNodeFor(path, write: false);
        if (!node.IsOK()) return ResultObjectFactory.CreateFailure<FileEntry[]>(node.Status);
        var names = VirtualTree.List(node.Value!.Path);
        if (!names.IsOK()) return ResultObjectFactory.CreateFailure<FileEntry[]>(names.Status);
        var entries = new List<FileEntry>();
        foreach (string name in names.Value!)
        {
            string child = node.Value.Path + "/" + name;
            entries.Add(new(child, VirtualTree.Find(child)!.Kind == VirtualKind.Directory));
        }

        entries.Sort(static (left, right) =>
        {
            int type = right.IsDirectory.CompareTo(left.IsDirectory);
            return type != 0 ? type : string.Compare(left.Path, right.Path, StringComparison.Ordinal);
        });
        return ResultObjectFactory.CreateSuccess(entries.ToArray());
    }

    /// <summary>Schreiben auf /dev/null: die Daten werden verworfen; jeder andere virtuelle Pfad ist nur lesbar.</summary>
    public ResultErr WriteNull(string? path)
    {
        var node = VirtualNodeFor(path, write: true);
        return node.IsOK() ? ResultErr.CreateSuccess() : node.Status;
    }

    private ResultObject<VirtualNode> VirtualNodeFor(string? path, bool write)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ResultObjectFactory.CreateFailure<VirtualNode>(ResultCode.InvalidArgument, "cause='InvalidPath' message='Ein Pfad muss angegeben werden.'");
        // Ohne Anmeldung zuerst ablehnen, damit auch die Existenz eines Namens nicht erkennbar ist.
        if (session?.UserId == null) return ResultObjectFactory.CreateFailure<VirtualNode>(FileAccessControl.Denied());
        var node = VirtualTree.Find(VirtualTree.Normalize(path, CurrentDirectory));
        if (node is null)
            return ResultObjectFactory.CreateFailure<VirtualNode>(ResultCode.NotFound, "cause='NotFound' message='Nicht gefunden.'");
        var allowed = VirtualAccess.Check(node, session, write);
        return allowed.IsOK() ? ResultObjectFactory.CreateSuccess(node) : ResultObjectFactory.CreateFailure<VirtualNode>(allowed);
    }

    /// <summary>Hoechstens <paramref name="maximumLength"/> + 1 Bytes, damit der Aufrufer die Ueberschreitung erkennt.</summary>
    private ResultObject<byte[]> ReadVirtualBytes(VirtualNode node, int maximumLength)
    {
        if (node.Kind == VirtualKind.Directory)
            return ResultObjectFactory.CreateFailure<byte[]>(ResultCode.InvalidArgument, "cause='IsDirectory' message='Ist ein Verzeichnis.'");
        if (node.Kind == VirtualKind.CharacterDevice)
        {
            var handle = DeviceFileSystem.Open(node.Path["/dev/".Length..]);
            if (!handle.IsOK()) return ResultObjectFactory.CreateFailure<byte[]>(handle.Status);
            if (AllocateBuffer(maximumLength + 1) is not { } data) return OutOfMemory<byte[]>();
            int total = 0;
            try
            {
                while (total < data.Length)
                {
                    var read = handle.Value!.Read(data.AsSpan(total));
                    if (!read.IsOK()) return ResultObjectFactory.CreateFailure<byte[]>(read.Status);
                    if (read.Value == 0) break;
                    total += read.Value;
                }
            }
            finally
            {
                handle.Value!.Close();
            }

            return ResultObjectFactory.CreateSuccess(data[..total]);
        }

        var unavailable = ResultObjectFactory.CreateFailure<byte[]>(ResultCode.Failure,
            "cause='SystemDataUnavailable' message='Systemdaten nicht verfuegbar.'");
        try
        {
            var snapshot = virtualSource?.Invoke(node.Path);
            return snapshot != null && snapshot.IsOK() && snapshot.Value != null ? snapshot : unavailable;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Ein werfender Erzeuger darf die Shell nicht beenden (Spec §2); die Ausnahme bleibt als Diagnose im Ergebnis.
            return ResultObjectFactory.CreateFromException<byte[]>(exception);
        }
    }

    /// <summary>
    /// Ersetzt den Inhalt einer Textdatei.
    /// </summary>
    public ResultErr WriteText(string? path, string? text, int maximumLength = MaximumTextLength)
    {
        if (text is null)
        {
            return ResultErr.CreateFailure(ResultCode.MissingArgument,
                "cause='InvalidInput' message='Der Dateiinhalt fehlt.'");
        }

        if (text.Length > maximumLength)
        {
            return TextTooLarge(maximumLength);
        }

        return StorageLock.Run(() => WriteDataStorageLocked(path, () => new UTF8Encoding(false, true).GetBytes(text), false));
    }

    /// <summary>Schreibt gepruefte Binaerdaten; auf Wunsch bleibt das Original als feste .bak-Datei erhalten.</summary>
    internal ResultErr WriteBytes(string path, byte[] data, bool keepBackup = false) => WriteBytes(path, data.AsMemory(), keepBackup);

    /// <summary>Wie oben fuer einen Ausschnitt eines Puffers, ohne ihn vorher zu kopieren (App-Speicher bis 16 MiB).</summary>
    internal ResultErr WriteBytes(string path, ReadOnlyMemory<byte> data, bool keepBackup = false)
    {
        return StorageLock.Run(() => WriteDataStorageLocked(path, () => data, keepBackup));
    }

    private ResultErr WriteDataStorageLocked(string? path, Func<ReadOnlyMemory<byte>> getData, bool keepBackup)
    {
        var resolved = ResolvePath(path, creating: true);
        if (!resolved.IsOK())
        {
            return resolved.Status;
        }

        string? temporaryPath = null;
        bool temporaryCreated = false;
        FileStream? temporaryStream = null;
        bool registered = false;
        bool backupRegistered = false;
        IDisposable? writer = null;
        try
        {
            writer = BeginWrite();
            registered = AuthorizeStorageLocked(resolved.Value!, FilePermission.Write, true);
            if (keepBackup) backupRegistered = AuthorizeStorageLocked(resolved.Value! + ".bak", FilePermission.Write, true);
            ReadOnlyMemory<byte> data = getData();
            temporaryPath = Path.Combine(Path.GetDirectoryName(resolved.Value!)!, $".zehos-{Guid.NewGuid():N}.tmp");
            EnsureDirectoryEntryAvailableStorageLocked(temporaryPath);
            // Ungepuffert, damit Schreibfehler vor dem Schliessen des Handles auftreten.
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            {
                temporaryStream = stream;
                temporaryCreated = true;
                stream.Write(data.Span);
                stream.Flush(true);
            }

            ReplaceFileStorageLocked(temporaryPath, resolved.Value!, keepBackup);
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            if (registered) access!.ForgetCreated(resolved.Value!);
            if (backupRegistered) access!.ForgetCreated(resolved.Value! + ".bak");
            if (temporaryCreated)
            {
                try
                {
                    // shortcut: Bei ENOSPC bleibt der Stream in Cosmos 3.0.94 offen; nach Runtime-Update erneut pruefen.
                    temporaryStream!.Dispose();
                    File.Delete(temporaryPath!);
                }
                catch (Exception cleanupException) when (FileErrors.IsFileError(cleanupException))
                {
                    return ResultErr.CreateFromException(new AggregateException(exception, cleanupException));
                }
            }

            return ResultErr.CreateFromException(exception);
        }
        finally { writer?.Dispose(); }
    }

    private IDisposable? BeginWrite() => tryBeginWrite == null ? null :
        tryBeginWrite() ?? throw new IOException(MigrationWriteGate.BusyMessage);

    private void ReplaceFileStorageLocked(string temporaryPath, string targetPath, bool keepBackup = false)
    {
        if (!File.Exists(targetPath))
        {
            moveFile(temporaryPath, targetPath, false);
            return;
        }

        // Cosmos haengt beim Ueberschreiben einen festen Sicherungsnamen an das Ziel an.
        string backupPath = keepBackup ? targetPath + ".bak" :
            Path.Combine(Path.GetDirectoryName(targetPath)!, $".zehos-{Guid.NewGuid():N}.bak");
        string? previousBackup = null;
        if (keepBackup && File.Exists(backupPath))
        {
            previousBackup = Path.Combine(Path.GetDirectoryName(targetPath)!, $".zehos-{Guid.NewGuid():N}.bak");
            moveFile(backupPath, previousBackup, false);
        }
        try
        {
            moveFile(targetPath, backupPath, false);
            try
            {
                moveFile(temporaryPath, targetPath, false);
            }
            catch (Exception exception) when (FileErrors.IsFileError(exception))
            {
                try { moveFile(backupPath, targetPath, false); }
                catch (Exception restoreException) when (FileErrors.IsFileError(restoreException))
                {
                    throw new IOException($"Wiederherstellung fehlgeschlagen. Die Originaldatei liegt unter '{backupPath}'.",
                        new AggregateException(exception, restoreException));
                }
                throw;
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            // Liegt das Original nach gescheiterter Wiederherstellung noch unter backupPath, bleibt die aeltere Sicherung unter ihrem Ausweichnamen.
            if (previousBackup != null && !File.Exists(backupPath))
            {
                try { moveFile(previousBackup, backupPath, false); }
                catch (Exception restoreException) when (FileErrors.IsFileError(restoreException))
                {
                    throw new IOException($"Vorherige Sicherung liegt unter '{previousBackup}'.", new AggregateException(exception, restoreException));
                }
            }
            throw;
        }

        if (!keepBackup) File.Delete(backupPath);
        if (previousBackup != null) File.Delete(previousBackup);
    }

    /// <summary>
    /// Erstellt ein Verzeichnis samt fehlenden Elternverzeichnissen.
    /// </summary>
    public ResultErr CreateDirectory(string? path) => StorageLock.Run(() => CreateDirectoryStorageLocked(path));

    private ResultErr CreateDirectoryStorageLocked(string? path)
    {
        var resolved = ResolvePath(path, creating: true);
        if (!resolved.IsOK())
        {
            return resolved.Status;
        }

        bool registered = false;
        IDisposable? writer = null;
        try
        {
            writer = BeginWrite();
            registered = AuthorizeStorageLocked(resolved.Value!, FilePermission.Write, true);
            EnsureDirectoryEntryAvailableStorageLocked(resolved.Value!, createParents: true);
            Directory.CreateDirectory(resolved.Value!);
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            if (registered) access!.ForgetCreated(resolved.Value!);
            return ResultErr.CreateFromException(exception);
        }
        finally { writer?.Dispose(); }
    }

    /// <summary>
    /// Wechselt nur bei einem erreichbaren Verzeichnis den Shellpfad.
    /// </summary>
    public ResultErr ChangeDirectory(string? path) => StorageLock.Run(() => ChangeDirectoryStorageLocked(path));

    private ResultErr ChangeDirectoryStorageLocked(string? path)
    {
        var resolved = ResolvePath(path);
        if (!resolved.IsOK())
        {
            return resolved.Status;
        }

        try
        {
            AuthorizeStorageLocked(resolved.Value!, FilePermission.Read);
            FileAttributes attributes = File.GetAttributes(resolved.Value!);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                return ResultErr.CreateFailure(ResultCode.InvalidArgument,
                    $"cause='NotDirectory' message='Kein Verzeichnis: {resolved.Value}'");
            }

            CurrentDirectory = resolved.Value!;
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultErr.CreateFromException(exception);
        }
    }

    public ResultErr CopyFile(string? source, string? target) => StorageLock.Run(() => TransferFileStorageLocked(source, target, false));
    public ResultErr MoveFile(string? source, string? target) => StorageLock.Run(() => TransferFileStorageLocked(source, target, true));

    private ResultErr TransferFileStorageLocked(string? source, string? target, bool moving)
    {
        var from = ResolvePath(source);
        if (!from.IsOK()) return from.Status;
        var to = ResolvePath(target, creating: true);
        if (!to.IsOK()) return to.Status;
        bool registered = false;
        string? temporary = null;
        FileStream? temporaryStream = null;
        IDisposable? writer = null;
        try
        {
            writer = BeginWrite();
            AuthorizeStorageLocked(from.Value!, moving ? FilePermission.Read | FilePermission.Write : FilePermission.Read);
            if (moving) AuthorizeStorageLocked(Path.GetDirectoryName(from.Value!)!, FilePermission.Write);
            // Oeffnen prueft auch fehlende Quellen, bevor Zielrechte angelegt werden.
            using (var input = new FileStream(from.Value!, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // Geraete liefern kein EOF (/dev/zero wuerde die Platte fuellen); geprueft vor jedem Ziel oder Rechte-Eintrag.
                if (!isRegularFile(from.Value!)) throw new IOException("Keine regulaere Datei (Geraet oder Sonderdatei).");
                if (access != null && (session == null || !access.Check(session, to.Value!, FilePermission.Write, true).IsOK()))
                    throw new UnauthorizedAccessException("Zugriff verweigert.");
                if (File.Exists(to.Value!) || Directory.Exists(to.Value!) || (access?.Contains(to.Value!) ?? false))
                    throw new IOException("Das Ziel existiert bereits.");
                // Gleichverzeichniges Rename verbraucht dauerhaft keinen weiteren Eintrag.
                if (moving && !string.Equals(Path.GetDirectoryName(from.Value!), Path.GetDirectoryName(to.Value!), StringComparison.Ordinal))
                    EnsureDirectoryEntryAvailableStorageLocked(to.Value!);
                if (moving && access != null)
                {
                    if (!access.PrepareMove(from.Value!, to.Value!).IsOK())
                        throw new UnauthorizedAccessException("Rechte konnten nicht gespeichert werden.");
                    registered = true;
                }
                else registered = AuthorizeStorageLocked(to.Value!, FilePermission.Write, true);
                if (!moving)
                {
                    temporary = Path.Combine(Path.GetDirectoryName(to.Value!)!, $".zehos-{Guid.NewGuid():N}.tmp");
                    EnsureDirectoryEntryAvailableStorageLocked(temporary);
                    using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1);
                    temporaryStream = output;
                    input.CopyTo(output, 4096);
                    output.Flush(true);
                }
            }
            moveFile(moving ? from.Value! : temporary!, to.Value!, false);
            temporary = null;
            if (moving && access != null) return access.RemoveEntry(from.Value!);
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            if (registered) access!.ForgetCreated(to.Value!);
            if (temporary != null)
            {
                try { temporaryStream?.Dispose(); File.Delete(temporary); }
                catch (Exception cleanupException) when (FileErrors.IsFileError(cleanupException))
                { return ResultErr.CreateFromException(new AggregateException(exception, cleanupException)); }
            }
            return ResultErr.CreateFromException(exception);
        }
        finally { writer?.Dispose(); }
    }

    public ResultErr DeleteFile(string? path) => StorageLock.Run(() => DeleteEntryStorageLocked(path, false));
    public ResultErr DeleteDirectory(string? path) => StorageLock.Run(() => DeleteEntryStorageLocked(path, true));

    private ResultErr DeleteEntryStorageLocked(string? path, bool directory)
    {
        var resolved = ResolvePath(path);
        if (!resolved.IsOK()) return resolved.Status;
        try
        {
            using var writer = BeginWrite();
            AuthorizeStorageLocked(resolved.Value!, FilePermission.Write);
            AuthorizeStorageLocked(Path.GetDirectoryName(resolved.Value!)!, FilePermission.Write);
            StringComparison comparison = access?.Comparison ?? StringComparison.OrdinalIgnoreCase;
            if (directory && (string.Equals(resolved.Value, CurrentDirectory, comparison) ||
                CurrentDirectory.StartsWith(Path.TrimEndingDirectorySeparator(resolved.Value!) + Path.DirectorySeparatorChar, comparison)))
                throw new IOException("Das Arbeitsverzeichnis oder dessen Elternverzeichnis darf nicht geloescht werden.");
            FileAttributes attributes = File.GetAttributes(resolved.Value!);
            if (((attributes & FileAttributes.Directory) != 0) != directory)
                throw new IOException(directory ? "Kein Verzeichnis." : "Keine Datei.");
            if (directory) Directory.Delete(resolved.Value!, false);
            else File.Delete(resolved.Value!);
            return access?.RemoveEntry(resolved.Value!) ?? ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        { return ResultErr.CreateFromException(exception); }
    }

    public ResultErr Touch(string? path) => StorageLock.Run(() => TouchStorageLocked(path));

    private ResultErr TouchStorageLocked(string? path)
    {
        var resolved = ResolvePath(path, creating: true);
        if (!resolved.IsOK()) return resolved.Status;
        bool registered = false;
        IDisposable? writer = null;
        try
        {
            writer = BeginWrite();
            registered = AuthorizeStorageLocked(resolved.Value!, FilePermission.Write, true);
            EnsureDirectoryEntryAvailableStorageLocked(resolved.Value!);
            using (var stream = new FileStream(resolved.Value!, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                stream.Flush(true);
            // Cosmos 3.0.94 bestaetigt Zeitstempel ohne Aenderung; auf dem Host werden sie aktualisiert.
            File.SetLastWriteTimeUtc(resolved.Value!, DateTime.UtcNow);
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            if (registered) access!.ForgetCreated(resolved.Value!);
            return ResultErr.CreateFromException(exception);
        }
        finally { writer?.Dispose(); }
    }

    public ResultObject<long> GetFileSize(string? path) => StorageLock.Run(() => GetFileSizeStorageLocked(path));

    private ResultObject<long> GetFileSizeStorageLocked(string? path)
    {
        var resolved = ResolvePath(path);
        if (!resolved.IsOK()) return ResultObjectFactory.CreateFailure<long>(resolved.Status);
        try
        {
            AuthorizeStorageLocked(resolved.Value!, FilePermission.Read);
            using var stream = new FileStream(resolved.Value!, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ResultObjectFactory.CreateSuccess(stream.Length);
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        { return ResultObjectFactory.CreateFromException<long>(exception); }
    }

    public ResultObject<(long TotalBytes, long AvailableBytes)> GetDiskSpace(string? path) => StorageLock.Run(() => GetDiskSpaceStorageLocked(path));

    private ResultObject<(long TotalBytes, long AvailableBytes)> GetDiskSpaceStorageLocked(string? path)
    {
        var resolved = ResolvePath(path);
        if (!resolved.IsOK()) return ResultObjectFactory.CreateFailure<(long, long)>(resolved.Status);
        try
        {
            AuthorizeStorageLocked(resolved.Value!, FilePermission.Read);
            _ = File.GetAttributes(resolved.Value!);
            return diskSpace?.Invoke(resolved.Value!) ??
                ResultObjectFactory.CreateFailure<(long, long)>(ResultCode.Failure, "Speicherstatistik ist ohne Dateisystemadapter nicht verfuegbar.");
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        { return ResultObjectFactory.CreateFromException<(long, long)>(exception); }
    }

    internal const int MaximumSearchEntries = 1024;

    public ResultObject<FileEntry[]> FindEntries(string? path, string name = "") => StorageLock.Run(() => FindEntriesStorageLocked(path, name));

    private ResultObject<FileEntry[]> FindEntriesStorageLocked(string? path, string name)
    {
        var resolved = ResolvePath(path);
        if (!resolved.IsOK()) return ResultObjectFactory.CreateFailure<FileEntry[]>(resolved.Status);
        var pending = new Stack<string>();
        var found = new List<FileEntry>();
        pending.Push(resolved.Value!);
        int visited = 0;
        try
        {
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                AuthorizeStorageLocked(directory, FilePermission.Read);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new NotSupportedException("Die Suche folgt keinen Verknuepfungen.");
                // Schrittweise lesen: Auch unsichtbare Eintraege duerfen das Arbeitsbudget nicht umgehen.
                foreach (string entryPath in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (++visited > MaximumSearchEntries)
                        return ResultObjectFactory.CreateFailure<FileEntry[]>(ResultCode.InvalidArgument, "Suche auf 1024 Eintraege begrenzt.");
                    // Vor GetAttributes: Cosmos folgt dort Symlinks.
                    if (!Visible(entryPath)) continue;
                    var attributes = File.GetAttributes(entryPath);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var entry = new FileEntry(entryPath, (attributes & FileAttributes.Directory) != 0);
                    if (Path.GetFileName(entry.Path).Contains(name, StringComparison.OrdinalIgnoreCase)) found.Add(entry);
                    if (entry.IsDirectory) pending.Push(entry.Path);
                }
            }
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        { return ResultObjectFactory.CreateFromException<FileEntry[]>(exception); }
        return ResultObjectFactory.CreateSuccess(found.ToArray());
    }

    public ResultObject<long> GetDirectorySize(string? path) => StorageLock.Run(() => GetDirectorySizeStorageLocked(path));

    private ResultObject<long> GetDirectorySizeStorageLocked(string? path)
    {
        var resolved = ResolvePath(path);
        if (!resolved.IsOK()) return ResultObjectFactory.CreateFailure<long>(resolved.Status);
        try
        {
            AuthorizeStorageLocked(resolved.Value!, FilePermission.Read);
            if ((File.GetAttributes(resolved.Value!) & FileAttributes.Directory) == 0) return GetFileSize(path);
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        { return ResultObjectFactory.CreateFromException<long>(exception); }
        var entries = FindEntries(path);
        if (!entries.IsOK()) return ResultObjectFactory.CreateFailure<long>(entries.Status);
        long size = 0;
        foreach (var entry in entries.Value!)
        {
            if (entry.IsDirectory) continue;
            var length = GetFileSize(entry.Path);
            if (!length.IsOK()) return length;
            size += length.Value;
        }
        return ResultObjectFactory.CreateSuccess(size);
    }

    /// <summary>
    /// Absoluter Pfad, erst nach dem Pfadwaechter (Etappe 3): jeder Symlink und jede fremde Mountgrenze wird abgelehnt,
    /// ein fehlendes Blatt nur bei <paramref name="creating"/>. Restrisiko: zwischen Pruefung und IO kann sich der Baum aendern
    /// (ZehOS selbst legt keine Symlinks an).
    /// </summary>
    internal ResultObject<string> ResolvePath(string? path, bool creating = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ResultObjectFactory.CreateFailure<string>(ResultCode.InvalidArgument,
                "cause='InvalidPath' message='Ein Pfad muss angegeben werden.'");
        }

        try
        {
            string full = Path.GetFullPath(path, CurrentDirectory);
            var guarded = FileAccessControl.RunGuard(pathGuard, full, creating);
            return guarded.IsOK() ? ResultObjectFactory.CreateSuccess(full) : ResultObjectFactory.CreateFailure<string>(guarded);
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<string>(exception);
        }
    }

    /// <summary>Hoststandard: vorhandener Eintrag ohne Verzeichnis-, Geraete- oder Reparse-Kennung; Fehler gelten als nicht regulaer.</summary>
    private static bool IsRegularHostFile(string path)
    {
        try
        {
            return StorageLock.Run(() => (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) == 0);
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return false;
        }
    }

    private void EnsureDirectoryEntryAvailableStorageLocked(string path, bool createParents = false)
    {
        if (directoryEntryLimit == null || File.Exists(path) || Directory.Exists(path)) return;
        string? parent = Path.GetDirectoryName(path);
        // mkdir -p benoetigt nur im ersten vorhandenen Elternordner einen neuen Eintrag; darunter sind die Ordner neu.
        while (createParents && parent != null && !Directory.Exists(parent)) parent = Path.GetDirectoryName(parent);
        if (parent == null || !Directory.Exists(parent)) return;
        int? maximum = directoryEntryLimit(parent);
        if (maximum == null) return;
        int count = 0;
        foreach (string _ in Directory.EnumerateFileSystemEntries(parent))
        {
            if (++count >= maximum.Value)
                throw new IOException($"cause='DirectoryEntryLimit' message='Verzeichnis {parent} ist auf {maximum.Value} Eintraege begrenzt; zuerst einen Eintrag entfernen oder einen anderen Ordner verwenden.'");
        }
    }

    private static ResultObject<T> OutOfMemory<T>() =>
        ResultObjectFactory.CreateFailure<T>(ResultCode.Failure, "cause='OutOfMemory' message='Kein Speicher fuer den Lesepuffer.'");

    /// <returns>true, wenn dafuer ein neuer Rechte-Eintrag angelegt wurde.</returns>
    private bool AuthorizeStorageLocked(string path, FilePermission permission, bool creating = false)
    {
        if (access == null) return false;
        if (session == null || !access.Check(session, path, permission, creating).IsOK())
            throw new UnauthorizedAccessException("Zugriff verweigert.");
        if (!creating || access.Contains(path)) return false;
        // Vorhandene, auch ueber FAT-Kurznamen erreichbare Eintraege muessen explizit zugeordnet sein.
        if (File.Exists(path) || Directory.Exists(path))
            throw new UnauthorizedAccessException("Vorhandener Eintrag ohne Rechtezuordnung.");
        if (!access.RegisterCreated(session, path).IsOK())
            throw new UnauthorizedAccessException("Rechte konnten nicht gespeichert werden; Zugriff gesperrt.");
        return true;
    }

    internal void ResetDirectory(string directory) => CurrentDirectory = directory;

    /// <summary>Ein Listeneintrag ist sichtbar nach Rechten, ohne Rechteverwaltung nach dem Pfadwaechter.</summary>
    private bool Visible(string entry) => access != null
        ? access.Check(session!, entry, FilePermission.Read).IsOK()
        : FileAccessControl.RunGuard(pathGuard, entry, false).IsOK();

    private static ResultErr TextTooLarge(int maximumLength)
    {
        return ResultErr.CreateFailure(ResultCode.InvalidArgument,
            $"cause='TextTooLarge' message='Textdateien sind auf {maximumLength} Zeichen begrenzt.'");
    }
}
