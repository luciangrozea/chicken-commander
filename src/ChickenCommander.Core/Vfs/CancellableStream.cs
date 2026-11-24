namespace ChickenCommander.Core.Vfs;

/// <summary>
/// Read-only pass-through stream that throws <see cref="OperationCanceledException"/> on the next read once
/// <paramref name="ct"/> is cancelled. Lets a library that reads a big file in one call be stopped.
/// </summary>
internal sealed class CancellableStream(Stream inner, CancellationToken ct) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ct.ThrowIfCancellationRequested();
        return inner.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        ct.ThrowIfCancellationRequested();
        return inner.Read(buffer);
    }

    public override int ReadByte()
    {
        ct.ThrowIfCancellationRequested();
        return inner.ReadByte();
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
