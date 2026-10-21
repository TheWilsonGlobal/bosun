using System.Net;
using System.Net.Sockets;
using Bosun.Protocols.Modbus;
using Microsoft.Extensions.Logging;

namespace Bosun.Simulator;

/// <summary>
/// The simulated PLC's Modbus TCP server. Serves FC03 (holding) and FC04 (input) from the same register bank; answers
/// exception 0x01 to any other function, 0x02 to a range outside the bank and 0x03 to a quantity of 0 or more than 125.
/// The unit id is echoed (the simulator is a single device, so it does not filter on it). Writes are not supported.
/// </summary>
public sealed class ModbusTcpServer(Func<ushort[]> snapshot, IPAddress address, int port, ILogger? logger = null) : IAsyncDisposable
{
    private readonly TcpListener listener = new(address, port);
    private readonly CancellationTokenSource stop = new();
    private readonly List<Task> handlers = [];
    private readonly List<TcpClient> clients = [];
    private readonly object gate = new();
    private Task? acceptLoop;
    private long requests;

    /// <summary>The bound port (useful when started on port 0).</summary>
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public long RequestsServed => Interlocked.Read(ref requests);

    public int ActiveConnections
    {
        get
        {
            lock (gate)
            {
                return clients.Count;
            }
        }
    }

    public void Start()
    {
        listener.Start();
        acceptLoop = Task.Run(AcceptLoopAsync);
        logger?.LogInformation("Modbus TCP server listening on {Endpoint}", listener.LocalEndpoint);
    }

    public async Task StopAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        lock (gate)
        {
            foreach (var client in clients)
            {
                client.Dispose(); // unblocks the pending reads
            }
        }

        if (acceptLoop is not null)
        {
            await acceptLoop;
        }

        Task[] pending;
        lock (gate)
        {
            pending = [.. handlers];
        }

        await Task.WhenAll(pending);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        stop.Dispose();
    }

    /// <summary>The pure request-to-response rule, public so it can be tested without a socket.</summary>
    public static byte[] Respond(ModbusRequest request, ushort[] bank)
    {
        if (request.FunctionCode is not ((byte)ModbusFunction.ReadHoldingRegisters or (byte)ModbusFunction.ReadInputRegisters))
        {
            return ModbusFrame.BuildExceptionResponse(request.TransactionId, request.UnitId, request.FunctionCode, ModbusExceptionCode.IllegalFunction);
        }

        if (request.Quantity is 0 or > ModbusFrame.MaxRegisters)
        {
            return ModbusFrame.BuildExceptionResponse(request.TransactionId, request.UnitId, request.FunctionCode, ModbusExceptionCode.IllegalDataValue);
        }

        if (request.Address + request.Quantity > bank.Length)
        {
            return ModbusFrame.BuildExceptionResponse(request.TransactionId, request.UnitId, request.FunctionCode, ModbusExceptionCode.IllegalDataAddress);
        }

        return ModbusFrame.BuildReadResponse(request.TransactionId, request.UnitId, request.FunctionCode, bank.AsSpan(request.Address, request.Quantity).ToArray());
    }

    private async Task AcceptLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            client.NoDelay = true;
            lock (gate)
            {
                clients.Add(client);
                handlers.Add(Task.Run(() => HandleAsync(client)));
            }
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            while (!stop.IsCancellationRequested)
            {
                var frame = await ModbusFraming.ReadFrameAsync(stream, stop.Token);
                if (frame is null)
                {
                    return; // peer closed
                }

                var request = ModbusFrame.ParseRequest(frame.Value.Header, frame.Value.Pdu);
                await stream.WriteAsync(Respond(request, snapshot()), stop.Token);
                Interlocked.Increment(ref requests);
            }
        }
        catch (Exception ex) when (ex is ModbusException or IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            if (ex is ModbusProtocolException)
            {
                logger?.LogWarning("Closing a connection after a malformed frame: {Message}", ex.Message);
            }
        }
        finally
        {
            lock (gate)
            {
                clients.Remove(client);
            }

            client.Dispose();
        }
    }
}
