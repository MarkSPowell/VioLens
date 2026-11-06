namespace Violens.ProxyCore.Http;

/// <summary>
/// Reading a stream with a limit on how much comes out of it: for
/// decompressors and archive entries, where a few kilobytes going in can
/// claim gigabytes coming out.
/// </summary>
internal static class ReadLimit
{
    /// <summary>
    /// Everything <paramref name="source"/> gives, refusing more than
    /// <paramref name="maxBytes"/> with <see cref="ReadLimitExceededException"/>,
    /// an <see cref="IOException"/> as a decompressor throws for bad data.
    /// </summary>
    public static byte[] ReadAll(Stream source, long maxBytes)
    {
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (destination.Length + read > maxBytes)
            {
                throw new ReadLimitExceededException(maxBytes);
            }

            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }

    /// <summary>A view of <paramref name="source"/> that stops once <paramref name="budget"/> is spent.</summary>
    public static Stream Limit(Stream source, ReadBudget budget) => new LimitedReadStream(source, budget);

    private sealed class LimitedReadStream(Stream source, ReadBudget budget) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => budget.Spend(source.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => budget.Spend(source.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            budget.Spend(await source.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                source.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>A number of bytes that can be read, shared by every stream given it.</summary>
internal sealed class ReadBudget(long bytes)
{
    private readonly long _limit = bytes;
    private long _remaining = bytes;

    /// <summary>Takes <paramref name="read"/> bytes from what's left, throwing once it's all gone.</summary>
    public int Spend(int read)
    {
        if (Interlocked.Add(ref _remaining, -read) < 0)
        {
            throw new ReadLimitExceededException(_limit);
        }

        return read;
    }
}

/// <summary>More came out of a stream than the limit allows.</summary>
internal sealed class ReadLimitExceededException(long limit)
    : IOException($"It's larger than the {limit / (1024 * 1024.0):0.#} MB VioLens reads.");
