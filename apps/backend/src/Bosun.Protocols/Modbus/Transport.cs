using System.Net.Sockets;

namespace Bosun.Protocols.Modbus;

/// <summary>
/// Opens the byte stream a protocol client talks over. The abstraction exists so the client can be tested against
/// captured frames (a scripted stream) as well as against a real socket.
/// </summary>
public interface IStreamConnector
{
    ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>A plain TCP connection (Nagle off: Modbus requests are tiny and latency matters more than packing).</summary>
public sealed class TcpStreamConnector(string host, int port, TimeSpan? connectTimeout = null) : IStreamConnector
{
    public string Host { get; } = host;

    public int Port { get; } = port;

    public async ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(connectTimeout ?? TimeSpan.FromSeconds(3));
            await client.ConnectAsync(Host, Port, timeout.Token);
            return new OwnedTcpStream(client);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            client.Dispose();
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new ModbusConnectionException($"Cannot connect to {Host}:{Port}: {ex.Message}", ex);
        }
    }

    /// <summary>A network stream that closes its socket when disposed.</summary>
    private sealed class OwnedTcpStream(TcpClient client) : Stream
    {
        private readonly NetworkStream inner = client.GetStream();

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                client.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
