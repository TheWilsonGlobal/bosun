using System.Buffers.Binary;

namespace Bosun.Protocols.Modbus;

/// <summary>
/// Encoding and decoding of Modbus TCP frames: MBAP header + PDU. All integers are big-endian.
/// <code>
/// MBAP  : transaction id (2) | protocol id (2, always 0) | length (2, = unit id + PDU) | unit id (1)
/// PDU   : function code (1) | data
///
/// Documented example (Modbus Application Protocol spec, "read holding registers"; unit 0x11, start 0x006B, 3 registers):
///   request    00 01 00 00 00 06 11 03 00 6B 00 03
///   response   00 01 00 00 00 09 11 03 06 02 2B 00 00 00 64        (values 0x022B, 0x0000, 0x0064)
///   exception  00 01 00 00 00 03 11 83 02                          (0x83 = 0x03 | 0x80, 0x02 = illegal data address)
/// </code>
/// </summary>
public static class ModbusFrame
{
    public const int MbapLength = 7;

    /// <summary>A read returns at most 125 registers (the PDU is limited to 253 bytes).</summary>
    public const int MaxRegisters = 125;

    /// <summary>Largest legal value of the MBAP length field: unit id + 253 PDU bytes.</summary>
    public const int MaxLengthField = 254;

    public const byte ExceptionFlag = 0x80;

    public static byte[] BuildReadRequest(ushort transactionId, byte unitId, ModbusFunction function, ushort address, ushort quantity)
    {
        var frame = new byte[MbapLength + 5];
        WriteMbap(frame, transactionId, length: 6, unitId);
        frame[7] = (byte)function;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), address);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10), quantity);
        return frame;
    }

    public static byte[] BuildReadResponse(ushort transactionId, byte unitId, byte functionCode, IReadOnlyList<ushort> values)
    {
        var byteCount = values.Count * 2;
        var frame = new byte[MbapLength + 2 + byteCount];
        WriteMbap(frame, transactionId, length: 3 + byteCount, unitId);
        frame[7] = functionCode;
        frame[8] = (byte)byteCount;
        for (var i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(9 + (i * 2)), values[i]);
        }

        return frame;
    }

    public static byte[] BuildExceptionResponse(ushort transactionId, byte unitId, byte functionCode, ModbusExceptionCode code)
    {
        var frame = new byte[MbapLength + 2];
        WriteMbap(frame, transactionId, length: 3, unitId);
        frame[7] = (byte)(functionCode | ExceptionFlag);
        frame[8] = (byte)code;
        return frame;
    }

    public static MbapHeader ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < MbapLength)
        {
            throw new ModbusProtocolException($"MBAP header needs {MbapLength} bytes, got {header.Length}.");
        }

        return new MbapHeader(
            BinaryPrimitives.ReadUInt16BigEndian(header),
            BinaryPrimitives.ReadUInt16BigEndian(header[2..]),
            BinaryPrimitives.ReadUInt16BigEndian(header[4..]),
            header[6]);
    }

    /// <summary>Validates the header fields that make a frame readable at all (protocol id 0, plausible length).</summary>
    public static void ValidateHeader(MbapHeader header)
    {
        if (header.ProtocolId != 0)
        {
            throw new ModbusProtocolException($"MBAP protocol id must be 0, got {header.ProtocolId}.");
        }

        if (header.Length is < 2 or > MaxLengthField)
        {
            throw new ModbusProtocolException($"MBAP length field {header.Length} is outside 2..{MaxLengthField}.");
        }
    }

    /// <summary>Server side: decodes a request PDU. FC03/FC04 carry address and quantity; any other function comes back with zeros (the server answers 0x01).</summary>
    public static ModbusRequest ParseRequest(MbapHeader header, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 1)
        {
            throw new ModbusProtocolException("Empty PDU.");
        }

        var function = pdu[0];
        if (function is (byte)ModbusFunction.ReadHoldingRegisters or (byte)ModbusFunction.ReadInputRegisters)
        {
            if (pdu.Length != 5)
            {
                throw new ModbusProtocolException($"Function 0x{function:X2} request PDU must be 5 bytes, got {pdu.Length}.");
            }

            return new ModbusRequest(header.TransactionId, header.UnitId, function,
                BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]), BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]));
        }

        return new ModbusRequest(header.TransactionId, header.UnitId, function, 0, 0);
    }

    /// <summary>
    /// Client side: checks a response against the request it answers and returns the register values.
    /// Throws <see cref="ModbusServerException"/> for an exception response and <see cref="ModbusProtocolException"/> for anything inconsistent.
    /// </summary>
    public static ushort[] ParseReadResponse(MbapHeader header, ReadOnlySpan<byte> pdu, ushort expectedTransactionId, byte expectedUnitId, ModbusFunction expectedFunction, ushort expectedQuantity)
    {
        if (header.TransactionId != expectedTransactionId)
        {
            throw new ModbusProtocolException($"Transaction id mismatch: expected {expectedTransactionId}, got {header.TransactionId}.");
        }

        if (header.UnitId != expectedUnitId)
        {
            throw new ModbusProtocolException($"Unit id mismatch: expected {expectedUnitId}, got {header.UnitId}.");
        }

        if (pdu.Length < 2)
        {
            throw new ModbusProtocolException($"Response PDU too short ({pdu.Length} bytes).");
        }

        var function = (byte)expectedFunction;
        if (pdu[0] == (function | ExceptionFlag))
        {
            if (pdu.Length != 2)
            {
                throw new ModbusProtocolException($"Exception response PDU must be 2 bytes, got {pdu.Length}.");
            }

            throw new ModbusServerException(function, (ModbusExceptionCode)pdu[1]);
        }

        if (pdu[0] != function)
        {
            throw new ModbusProtocolException($"Function code mismatch: expected 0x{function:X2}, got 0x{pdu[0]:X2}.");
        }

        var byteCount = pdu[1];
        if (byteCount != expectedQuantity * 2 || pdu.Length != 2 + byteCount)
        {
            throw new ModbusProtocolException(
                $"Byte count {byteCount} / PDU length {pdu.Length} do not match the {expectedQuantity} registers requested.");
        }

        var values = new ushort[expectedQuantity];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu[(2 + (i * 2))..]);
        }

        return values;
    }

    private static void WriteMbap(Span<byte> frame, ushort transactionId, int length, byte unitId)
    {
        BinaryPrimitives.WriteUInt16BigEndian(frame, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame[2..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame[4..], (ushort)length);
        frame[6] = unitId;
    }
}
