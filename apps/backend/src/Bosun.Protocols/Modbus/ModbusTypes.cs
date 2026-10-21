namespace Bosun.Protocols.Modbus;

/// <summary>The two read functions Bosun uses (everything else is refused by the simulator with exception 0x01).</summary>
public enum ModbusFunction : byte
{
    ReadHoldingRegisters = 0x03,
    ReadInputRegisters = 0x04,
}

/// <summary>Exception codes of the Modbus application protocol (spec section 7). A response carries them with function code | 0x80.</summary>
public enum ModbusExceptionCode : byte
{
    IllegalFunction = 0x01,
    IllegalDataAddress = 0x02,
    IllegalDataValue = 0x03,
    ServerDeviceFailure = 0x04,
    Acknowledge = 0x05,
    ServerDeviceBusy = 0x06,
    GatewayPathUnavailable = 0x0A,
    GatewayTargetDeviceFailedToRespond = 0x0B,
}

/// <summary>Base of every failure of the Modbus client. The connection is dropped for all of them except <see cref="ModbusServerException"/>.</summary>
public class ModbusException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The server answered with an exception response (function code | 0x80). The connection itself is healthy.</summary>
public sealed class ModbusServerException(byte functionCode, ModbusExceptionCode code)
    : ModbusException($"Modbus exception response to function 0x{functionCode:X2}: 0x{(byte)code:X2} ({code}).")
{
    public byte FunctionCode { get; } = functionCode;

    public ModbusExceptionCode Code { get; } = code;
}

/// <summary>The bytes on the wire are not a valid Modbus TCP frame, or do not answer the request (id, unit, function or length mismatch).</summary>
public sealed class ModbusProtocolException(string message) : ModbusException(message);

/// <summary>No complete response arrived within the response timeout. The connection is dropped: a late reply would desynchronise the next request.</summary>
public sealed class ModbusTimeoutException(TimeSpan timeout) : ModbusException($"No Modbus response within {timeout.TotalMilliseconds:0} ms.");

/// <summary>The transport failed: connect refused, reset, or the peer closed the stream.</summary>
public sealed class ModbusConnectionException(string message, Exception? inner = null) : ModbusException(message, inner);

/// <summary>A parsed request (server side).</summary>
public readonly record struct ModbusRequest(ushort TransactionId, byte UnitId, byte FunctionCode, ushort Address, ushort Quantity);

/// <summary>The MBAP header (7 bytes) in front of every Modbus TCP frame.</summary>
public readonly record struct MbapHeader(ushort TransactionId, ushort ProtocolId, ushort Length, byte UnitId);
