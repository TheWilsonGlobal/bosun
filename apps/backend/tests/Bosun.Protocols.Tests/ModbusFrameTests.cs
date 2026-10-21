using Bosun.Protocols.Modbus;

namespace Bosun.Protocols.Tests;

public class ModbusFrameTests
{
    // Documented example frames (Modbus Application Protocol spec, read holding registers, unit 0x11, start 0x006B, 3 registers).
    private const string DocRequest = "00 01 00 00 00 06 11 03 00 6B 00 03";
    private const string DocResponse = "00 01 00 00 00 09 11 03 06 02 2B 00 00 00 64";
    private const string DocException = "00 01 00 00 00 03 11 83 02";

    [Fact]
    public void Fc03_request_is_byte_exact()
    {
        var frame = ModbusFrame.BuildReadRequest(1, 0x11, ModbusFunction.ReadHoldingRegisters, 0x006B, 3);
        Hex.Of(frame).Should().Be(DocRequest);
    }

    [Fact]
    public void Fc04_request_is_byte_exact()
    {
        // same layout, function 0x04: transaction 0x1234, unit 1, address 0, 10 registers
        var frame = ModbusFrame.BuildReadRequest(0x1234, 1, ModbusFunction.ReadInputRegisters, 0, 10);
        Hex.Of(frame).Should().Be("12 34 00 00 00 06 01 04 00 00 00 0A");
    }

    [Fact]
    public void Fc03_response_is_byte_exact_and_round_trips()
    {
        var frame = ModbusFrame.BuildReadResponse(1, 0x11, 0x03, [0x022B, 0x0000, 0x0064]);
        Hex.Of(frame).Should().Be(DocResponse);

        var header = ModbusFrame.ParseHeader(frame);
        header.Should().Be(new MbapHeader(1, 0, 9, 0x11));
        var values = ModbusFrame.ParseReadResponse(header, frame.AsSpan(7), 1, 0x11, ModbusFunction.ReadHoldingRegisters, 3);
        values.Should().Equal(0x022B, 0x0000, 0x0064);
    }

    [Fact]
    public void Exception_response_is_byte_exact()
    {
        // 0x83 = function 0x03 | 0x80, 0x02 = illegal data address
        var frame = ModbusFrame.BuildExceptionResponse(1, 0x11, 0x03, ModbusExceptionCode.IllegalDataAddress);
        Hex.Of(frame).Should().Be(DocException);

        var act = () => ModbusFrame.ParseReadResponse(ModbusFrame.ParseHeader(frame), frame.AsSpan(7), 1, 0x11, ModbusFunction.ReadHoldingRegisters, 3);
        act.Should().Throw<ModbusServerException>().Which.Should().Match<ModbusServerException>(e =>
            e.Code == ModbusExceptionCode.IllegalDataAddress && e.FunctionCode == 0x03);
    }

    [Fact]
    public void Request_parses_back()
    {
        var frame = Hex.Bytes(DocRequest);
        var request = ModbusFrame.ParseRequest(ModbusFrame.ParseHeader(frame), frame.AsSpan(7));
        request.Should().Be(new ModbusRequest(1, 0x11, 0x03, 0x006B, 3));
    }

    [Fact]
    public void Unsupported_function_parses_with_zero_address_so_the_server_can_answer_0x01()
    {
        var frame = Hex.Bytes("00 07 00 00 00 06 01 06 00 01 00 03"); // FC06 write single register
        var request = ModbusFrame.ParseRequest(ModbusFrame.ParseHeader(frame), frame.AsSpan(7));
        request.FunctionCode.Should().Be(0x06);
        request.Quantity.Should().Be(0);
    }

    [Fact]
    public void Read_request_with_wrong_pdu_length_is_a_protocol_error()
    {
        var frame = Hex.Bytes("00 01 00 00 00 04 01 03 00 00"); // PDU of 3 bytes
        var act = () => ModbusFrame.ParseRequest(ModbusFrame.ParseHeader(frame), frame.AsSpan(7));
        act.Should().Throw<ModbusProtocolException>();
    }

    [Theory]
    [InlineData("00 01 00 01 00 06 11 03 00 6B 00 03", "protocol id")] // protocol id 1
    [InlineData("00 01 00 00 00 01 11", "length")] // length 1: no PDU
    [InlineData("00 01 00 00 00 FF 11", "length")] // length 255 > 254
    public void Bad_headers_are_rejected(string hex, string mention)
    {
        var header = ModbusFrame.ParseHeader(Hex.Bytes(hex).AsSpan(0, 7));
        var act = () => ModbusFrame.ValidateHeader(header);
        act.Should().Throw<ModbusProtocolException>().WithMessage($"*{mention}*");
    }

    [Fact]
    public void Header_shorter_than_seven_bytes_is_rejected()
    {
        var act = () => ModbusFrame.ParseHeader(new byte[6]);
        act.Should().Throw<ModbusProtocolException>();
    }

    [Theory]
    [InlineData("00 02 00 00 00 09 11 03 06 02 2B 00 00 00 64", "Transaction id mismatch")] // answers transaction 2, not 1
    [InlineData("00 01 00 00 00 09 12 03 06 02 2B 00 00 00 64", "Unit id mismatch")]
    [InlineData("00 01 00 00 00 09 11 04 06 02 2B 00 00 00 64", "Function code mismatch")]
    [InlineData("00 01 00 00 00 07 11 03 04 02 2B 00 00", "do not match")] // 2 registers where 3 were asked
    [InlineData("00 01 00 00 00 09 11 03 08 02 2B 00 00 00 64", "do not match")] // byte count lies
    public void Inconsistent_responses_are_protocol_errors(string hex, string message)
    {
        var frame = Hex.Bytes(hex);
        var act = () => ModbusFrame.ParseReadResponse(ModbusFrame.ParseHeader(frame), frame.AsSpan(7), 1, 0x11, ModbusFunction.ReadHoldingRegisters, 3);
        act.Should().Throw<ModbusProtocolException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void Register_scaling_is_signed_int16()
    {
        var entry = new RegisterEntry(0, "X", Scale: 10, "degC");
        entry.ToRaw(80.04).Should().Be(800);
        entry.ToEngineering(800).Should().Be(80.0);
        entry.ToRaw(-5.5).Should().Be(unchecked((ushort)-55));
        entry.ToEngineering(unchecked((ushort)-55)).Should().Be(-5.5);
        entry.ToRaw(1e9).Should().Be((ushort)short.MaxValue); // clamps instead of wrapping
    }

    [Fact]
    public void Register_map_validation_and_blocks()
    {
        var map = RegisterMap.Parse("""
            { "unitId": 3, "registers": [
              { "address": 0, "tag": "A", "scale": 10, "unit": "x" },
              { "address": 1, "tag": "B", "scale": 1 },
              { "address": 5, "tag": "C", "scale": 100, "bank": "input" },
              { "address": 6, "tag": "D", "scale": 1 } ] }
            """);
        map.UnitId.Should().Be(3);
        map.Size.Should().Be(7);
        map.Blocks().Select(b => (b.Bank, b.Address, b.Count)).Should().Equal(
            (RegisterBank.Holding, 0, 2), (RegisterBank.Holding, 6, 1), (RegisterBank.Input, 5, 1));
        map.Blocks(maxRegisters: 1).Should().HaveCount(4);

        var dup = () => RegisterMap.Parse("""{ "registers": [ { "address": 0, "tag": "A", "scale": 1 }, { "address": 1, "tag": "A", "scale": 1 } ] }""");
        dup.Should().Throw<FormatException>();
        var zero = () => RegisterMap.Parse("""{ "registers": [ { "address": 0, "tag": "A", "scale": 0 } ] }""");
        zero.Should().Throw<FormatException>();
    }

    [Fact]
    public void The_shared_register_map_file_loads_with_about_forty_unique_tags()
    {
        var path = RegisterMap.FindDefaultFile(AppContext.BaseDirectory);
        path.Should().NotBeNull();
        var map = RegisterMap.Load(path!);
        map.Registers.Count.Should().BeInRange(36, 44);
        map.Registers.Select(r => r.Tag).Should().OnlyHaveUniqueItems();
        map.Registers.Where(r => r.Tag.StartsWith("ME-EXH-TT", StringComparison.Ordinal)).Should().HaveCount(8);
        map.Blocks().Should().HaveCount(1, "one read polls the whole bank");
    }
}
