using System;
using System.IO;
using System.Threading;

namespace ProjektZeh.Storage.Migration;

/// <summary>Prueft Abbruch auch innerhalb von HashData/CopyTo und schliesst den uebernommenen Lesestrom.</summary>
internal sealed class CancellationReadStream(Stream inner, CancellationToken cancellation) : Stream
{
    private const int MaximumRead = 64 * 1024;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        cancellation.ThrowIfCancellationRequested();
        return inner.Read(buffer, offset, Math.Min(count, MaximumRead));
    }

    public override int Read(Span<byte> buffer)
    {
        cancellation.ThrowIfCancellationRequested();
        return inner.Read(buffer[..Math.Min(buffer.Length, MaximumRead)]);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
