namespace Bosun.Protocols.Modbus;

/// <summary>
/// Reads whole frames from a byte stream. TCP is a stream, not a message transport: one frame may arrive in several reads
/// (fragmentation) and one read may hold several frames, so every read loops until the exact byte count is in.
/// </summary>
public static class ModbusFraming
{
    /// <summary>
    /// Reads one frame (7 byte header, then length-1 PDU bytes). Returns null on a clean end of stream before the first byte;
    /// throws <see cref="ModbusConnectionException"/> when the stream ends in the middle of a frame.
    /// </summary>
    public static async Task<(MbapHeader Header, byte[] Pdu)?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var headerBytes = new byte[ModbusFrame.MbapLength];
        if (!await ReadExactlyOrEndAsync(stream, headerBytes, cancellationToken))
        {
            return null;
        }

        var header = ModbusFrame.ParseHeader(headerBytes);
        ModbusFrame.ValidateHeader(header);

        var pdu = new byte[header.Length - 1];
        if (!await ReadExactlyOrEndAsync(stream, pdu, cancellationToken))
        {
            throw new ModbusConnectionException("Stream closed in the middle of a frame (PDU missing).");
        }

        return (header, pdu);
    }

    /// <summary>Fills <paramref name="buffer"/>. False when the stream ended before the first byte; throws when it ended part way.</summary>
    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (n == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new ModbusConnectionException($"Stream closed after {read} of {buffer.Length} bytes.");
            }

            read += n;
        }

        return true;
    }
}
