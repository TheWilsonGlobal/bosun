namespace Bosun.Protocols.Modbus;

public sealed record ModbusClientOptions
{
    /// <summary>How long a request may wait for its complete response before the connection is dropped.</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// A small Modbus TCP client (function codes 03 and 04). One request is in flight at a time: the transaction id is still
/// checked on every response, so a reply that does not belong to the request is an error rather than silently wrong data.
/// After a timeout or any protocol error the connection is dropped (a late or garbled reply would desynchronise the next
/// request); an exception response from the server leaves it open. Reconnect timing is NOT here, see <see cref="ReconnectPolicy"/>.
/// </summary>
public sealed class ModbusClient(IStreamConnector connector, ModbusClientOptions? options = null) : IAsyncDisposable
{
    private readonly ModbusClientOptions options = options ?? new ModbusClientOptions();
    private readonly SemaphoreSlim requestLock = new(1, 1);
    private Stream? stream;
    private ushort lastTransactionId;

    public bool IsConnected => stream is not null;

    /// <summary>The id the next request will use (exposed so tests can predict the bytes on the wire).</summary>
    public ushort NextTransactionId => unchecked((ushort)(lastTransactionId + 1));

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await requestLock.WaitAsync(cancellationToken);
        try
        {
            Drop();
            stream = await connector.ConnectAsync(cancellationToken);
        }
        finally
        {
            requestLock.Release();
        }
    }

    public Task<ushort[]> ReadHoldingRegistersAsync(byte unitId, ushort address, ushort quantity, CancellationToken cancellationToken) =>
        ReadAsync(ModbusFunction.ReadHoldingRegisters, unitId, address, quantity, cancellationToken);

    public Task<ushort[]> ReadInputRegistersAsync(byte unitId, ushort address, ushort quantity, CancellationToken cancellationToken) =>
        ReadAsync(ModbusFunction.ReadInputRegisters, unitId, address, quantity, cancellationToken);

    public async Task<ushort[]> ReadAsync(ModbusFunction function, byte unitId, ushort address, ushort quantity, CancellationToken cancellationToken)
    {
        if (quantity is 0 or > ModbusFrame.MaxRegisters)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, $"A read covers 1..{ModbusFrame.MaxRegisters} registers.");
        }

        if (address + quantity > 0x10000)
        {
            throw new ArgumentOutOfRangeException(nameof(address), address, "The register range runs past 65535.");
        }

        await requestLock.WaitAsync(cancellationToken);
        try
        {
            var current = stream ?? throw new ModbusConnectionException("Not connected.");
            var transactionId = unchecked(++lastTransactionId);
            var request = ModbusFrame.BuildReadRequest(transactionId, unitId, function, address, quantity);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.ResponseTimeout);
            try
            {
                await current.WriteAsync(request, timeout.Token);
                await current.FlushAsync(timeout.Token);
                var frame = await ModbusFraming.ReadFrameAsync(current, timeout.Token)
                    ?? throw new ModbusConnectionException("The server closed the connection.");
                return ModbusFrame.ParseReadResponse(frame.Header, frame.Pdu, transactionId, unitId, function, quantity);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Drop();
                throw new ModbusTimeoutException(options.ResponseTimeout);
            }
            catch (ModbusServerException)
            {
                throw; // the server understood us and refused: the connection is fine
            }
            catch (ModbusException)
            {
                Drop();
                throw;
            }
            catch (IOException ex)
            {
                Drop();
                throw new ModbusConnectionException($"Connection lost: {ex.Message}", ex);
            }
            catch (OperationCanceledException)
            {
                Drop(); // the caller cancelled mid-request: an unread reply may still arrive
                throw;
            }
        }
        finally
        {
            requestLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await requestLock.WaitAsync();
        try
        {
            Drop();
        }
        finally
        {
            requestLock.Release();
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private void Drop()
    {
        var old = stream;
        stream = null;
        old?.Dispose();
    }
}
