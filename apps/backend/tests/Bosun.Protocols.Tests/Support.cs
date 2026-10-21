using Bosun.Protocols.Modbus;

namespace Bosun.Protocols.Tests;

public static class Hex
{
    /// <summary>"00 01 00 00 00 06 11 03 00 6B 00 03" to bytes.</summary>
    public static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    public static string Of(byte[] bytes) => string.Join(' ', bytes.Select(b => b.ToString("X2")));
}

/// <summary>
/// A stream that plays back captured bytes in chosen fragments and records what was written. Reads can also be made to hang
/// (until cancelled) or to end the stream, which is how timeouts and mid-frame disconnects are scripted.
/// </summary>
public sealed class ScriptedStream : Stream
{
    private readonly LinkedList<byte[]> chunks = new();
    private readonly List<byte> written = [];
    private readonly SemaphoreSlim available = new(0);
    private bool ended;

    public int WriteCount { get; private set; }

    public byte[] Written => [.. written];

    public bool Disposed { get; private set; }

    /// <summary>Queue bytes; each call is one read result (a short read when it is smaller than the caller's buffer).</summary>
    public ScriptedStream Feed(params byte[][] fragments)
    {
        foreach (var fragment in fragments)
        {
            chunks.AddLast(fragment);
            available.Release();
        }

        return this;
    }

    /// <summary>Queue bytes one per read: the worst-case fragmentation.</summary>
    public ScriptedStream FeedByteByByte(byte[] bytes) => Feed(bytes.Select(b => new[] { b }).ToArray());

    /// <summary>After the queued bytes, reads return 0 (the peer closed the connection).</summary>
    public ScriptedStream ThenEnd()
    {
        ended = true;
        available.Release();
        return this;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await available.WaitAsync(cancellationToken);
        if (chunks.Count == 0)
        {
            available.Release(); // an ended stream keeps answering 0
            return ended ? 0 : throw new InvalidOperationException("Script exhausted.");
        }

        var first = chunks.First!;
        var n = Math.Min(buffer.Length, first.Value.Length);
        first.Value.AsSpan(0, n).CopyTo(buffer.Span);
        if (n == first.Value.Length)
        {
            chunks.RemoveFirst();
        }
        else
        {
            first.Value = first.Value[n..];
            available.Release(); // the rest of this chunk is another read
        }

        return n;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        written.AddRange(buffer.ToArray());
        WriteCount++;
        return ValueTask.CompletedTask;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

public sealed class ScriptedConnector(params ScriptedStream[] streams) : IStreamConnector
{
    private readonly Queue<ScriptedStream> queue = new(streams);

    public int Connects { get; private set; }

    public ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        Connects++;
        return queue.TryDequeue(out var next)
            ? ValueTask.FromResult<Stream>(next)
            : throw new ModbusConnectionException("No scripted connection left.");
    }
}

/// <summary>A TimeProvider that only moves when told to.</summary>
public sealed class ManualTime : TimeProvider
{
    private long ticks;
    private DateTimeOffset utc = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Interlocked.Read(ref ticks);

    public override DateTimeOffset GetUtcNow() => utc;

    public void Advance(TimeSpan by)
    {
        Interlocked.Add(ref ticks, (long)by.TotalMilliseconds);
        utc += by;
    }
}
