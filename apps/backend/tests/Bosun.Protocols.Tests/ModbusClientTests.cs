using Bosun.Protocols.Modbus;

namespace Bosun.Protocols.Tests;

public class ModbusClientTests
{
    private const string DocRequest = "00 01 00 00 00 06 11 03 00 6B 00 03";
    private const string DocResponse = "00 01 00 00 00 09 11 03 06 02 2B 00 00 00 64";

    private static async Task<(ModbusClient Client, ScriptedStream Stream)> ConnectedAsync(Action<ScriptedStream> script, TimeSpan? timeout = null)
    {
        var stream = new ScriptedStream();
        script(stream);
        var client = new ModbusClient(new ScriptedConnector(stream), new ModbusClientOptions { ResponseTimeout = timeout ?? TimeSpan.FromSeconds(2) });
        await client.ConnectAsync(CancellationToken.None);
        return (client, stream);
    }

    [Fact]
    public async Task Reads_holding_registers_and_sends_the_documented_request_bytes()
    {
        var (client, stream) = await ConnectedAsync(s => s.Feed(Hex.Bytes(DocResponse)));

        var values = await client.ReadHoldingRegistersAsync(0x11, 0x006B, 3, CancellationToken.None);

        values.Should().Equal(0x022B, 0x0000, 0x0064);
        Hex.Of(stream.Written).Should().Be(DocRequest);
        client.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task Reads_input_registers_with_function_04()
    {
        var (client, stream) = await ConnectedAsync(s => s.Feed(Hex.Bytes("00 01 00 00 00 05 01 04 02 12 34")));

        var values = await client.ReadInputRegistersAsync(1, 7, 1, CancellationToken.None);

        values.Should().Equal(0x1234);
        Hex.Of(stream.Written).Should().Be("00 01 00 00 00 06 01 04 00 07 00 01");
    }

    [Fact]
    public async Task Transaction_ids_increment_per_request()
    {
        var (client, stream) = await ConnectedAsync(s => s
            .Feed(Hex.Bytes("00 01 00 00 00 05 01 03 02 00 0A"))
            .Feed(Hex.Bytes("00 02 00 00 00 05 01 03 02 00 0B")));

        (await client.ReadHoldingRegistersAsync(1, 0, 1, CancellationToken.None)).Should().Equal(10);
        (await client.ReadHoldingRegistersAsync(1, 0, 1, CancellationToken.None)).Should().Equal(11);
        Hex.Of(stream.Written).Should().Be("00 01 00 00 00 06 01 03 00 00 00 01 00 02 00 00 00 06 01 03 00 00 00 01");
    }

    [Fact]
    public async Task Response_split_byte_by_byte_is_reassembled()
    {
        var (client, _) = await ConnectedAsync(s => s.FeedByteByByte(Hex.Bytes(DocResponse)));

        var values = await client.ReadHoldingRegistersAsync(0x11, 0x006B, 3, CancellationToken.None);

        values.Should().Equal(0x022B, 0x0000, 0x0064);
    }

    [Fact]
    public async Task Header_and_pdu_in_separate_segments_are_reassembled()
    {
        var frame = Hex.Bytes(DocResponse);
        var (client, _) = await ConnectedAsync(s => s.Feed(frame[..3], frame[3..7], frame[7..10], frame[10..]));

        (await client.ReadHoldingRegistersAsync(0x11, 0x006B, 3, CancellationToken.None)).Should().Equal(0x022B, 0x0000, 0x0064);
    }

    [Fact]
    public async Task Illegal_data_address_exception_is_surfaced_and_keeps_the_connection()
    {
        var (client, _) = await ConnectedAsync(s => s
            .Feed(Hex.Bytes("00 01 00 00 00 03 01 83 02")) // 0x83 = FC03 | 0x80, code 02
            .Feed(Hex.Bytes("00 02 00 00 00 05 01 03 02 00 07")));

        var act = () => client.ReadHoldingRegistersAsync(1, 9999, 2, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ModbusServerException>()).Which;
        ex.Code.Should().Be(ModbusExceptionCode.IllegalDataAddress);
        ex.FunctionCode.Should().Be(0x03);
        client.IsConnected.Should().BeTrue("an exception response means the server is alive and well");
        (await client.ReadHoldingRegistersAsync(1, 0, 1, CancellationToken.None)).Should().Equal(7);
    }

    [Theory]
    [InlineData("00 01 00 00 00 03 01 83 01", ModbusExceptionCode.IllegalFunction)]
    [InlineData("00 01 00 00 00 03 01 83 03", ModbusExceptionCode.IllegalDataValue)]
    [InlineData("00 01 00 00 00 03 01 83 04", ModbusExceptionCode.ServerDeviceFailure)]
    [InlineData("00 01 00 00 00 03 01 83 06", ModbusExceptionCode.ServerDeviceBusy)]
    public async Task Other_exception_codes_map_to_the_enum(string hex, ModbusExceptionCode code)
    {
        var (client, _) = await ConnectedAsync(s => s.Feed(Hex.Bytes(hex)));

        var act = () => client.ReadHoldingRegistersAsync(1, 0, 1, CancellationToken.None);

        (await act.Should().ThrowAsync<ModbusServerException>()).Which.Code.Should().Be(code);
    }

    [Fact]
    public async Task An_exception_response_for_a_different_function_is_a_protocol_error()
    {
        var (client, _) = await ConnectedAsync(s => s.Feed(Hex.Bytes("00 01 00 00 00 03 01 84 02"))); // FC04 exception to an FC03 request

        var act = () => client.ReadHoldingRegistersAsync(1, 0, 1, CancellationToken.None);

        await act.Should().ThrowAsync<ModbusProtocolException>().WithMessage("*Function code mismatch*");
    }

    [Fact]
    public async Task Transaction_id_mismatch_is_an_error_and_drops_the_connection()
    {
        var (client, stream) = await ConnectedAsync(s => s.Feed(Hex.Bytes("00 09 00 00 00 05 01 03 02 00 0A"))); // answers 9, asked 1

        var act = () => client.ReadHoldingRegistersAsync(1, 0, 1, CancellationToken.None);

        await act.Should().ThrowAsync<ModbusProtocolException>().WithMessage("*Transaction id mismatch*");
        client.IsConnected.Should().BeFalse();
        stream.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task A_missing_response_times_out_and_drops_the_connection()
    {
        var (client, stream) = await ConnectedAsync(_ => { }, timeout: TimeSpan.FromMilliseconds(150));

        var act = () => client.ReadHoldingRegistersAsync(1, 0, 1, CancellationToken.None);

        await act.Should().ThrowAsync<ModbusTimeoutException>();
        client.IsConnected.Should().BeFalse("a late reply must not be read as the answer to the next request");
        stream.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task A_half_frame_followed_by_silence_times_out()
    {
        var (client, _) = await ConnectedAsync(s => s.Feed(Hex.Bytes("00 01 00 00 00 09 11 03")), timeout: TimeSpan.FromMilliseconds(150));

        var act = () => client.ReadHoldingRegistersAsync(0x11, 0, 3, CancellationToken.None);

        await act.Should().ThrowAsync<ModbusTimeoutException>();
    }

    [Fact]
    public async Task A_stream_that_ends_mid_frame_is_a_connection_error()
    {
        var (client, _) = await ConnectedAsync(s => s.Feed(Hex.Bytes("00 01 00 00 00 09 11 03 06 02")).ThenEnd());

        var act = () => client.ReadHoldingRegistersAsync(0x11, 0, 3, CancellationToken.None);

        await act.Should().ThrowAsync<ModbusConnectionException>();
        client.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task A_clean_close_before_any_response_is_a_connection_error()
    {
        var (client, _) = await ConnectedAsync(s => s.ThenEnd());

        var act = () => client.ReadHoldingRegistersAsync(1, 0, 1, CancellationToken.None);

        await act.Should().ThrowAsync<ModbusConnectionException>().WithMessage("*closed*");
    }

    [Fact]
    public async Task Garbage_header_is_a_protocol_error()
    {
        var (client, _) = await ConnectedAsync(s => s.Feed(Hex.Bytes("00 01 12 34 00 05 01 03 02 00 0A"))); // protocol id 0x1234

        var act = () => client.ReadHoldingRegistersAsync(1, 0, 1, CancellationToken.None);

        await act.Should().ThrowAsync<ModbusProtocolException>().WithMessage("*protocol id*");
    }

    [Fact]
    public async Task Reading_before_connecting_fails_cleanly()
    {
        var client = new ModbusClient(new ScriptedConnector());

        var act = () => client.ReadHoldingRegistersAsync(1, 0, 1, CancellationToken.None);

        await act.Should().ThrowAsync<ModbusConnectionException>().WithMessage("*Not connected*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(126)]
    public async Task Quantity_outside_1_to_125_is_refused_before_anything_is_sent(int quantity)
    {
        var (client, stream) = await ConnectedAsync(_ => { });

        var act = () => client.ReadHoldingRegistersAsync(1, 0, (ushort)quantity, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        stream.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task Cancelling_mid_request_drops_the_connection_and_propagates_cancellation()
    {
        var (client, _) = await ConnectedAsync(_ => { });
        using var cts = new CancellationTokenSource(100);

        var act = () => client.ReadHoldingRegistersAsync(1, 0, 1, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        client.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task Connect_replaces_the_previous_stream()
    {
        var first = new ScriptedStream();
        var second = new ScriptedStream().Feed(Hex.Bytes("00 01 00 00 00 05 01 03 02 00 0A"));
        var client = new ModbusClient(new ScriptedConnector(first, second));

        await client.ConnectAsync(CancellationToken.None);
        await client.ConnectAsync(CancellationToken.None);

        first.Disposed.Should().BeTrue();
        (await client.ReadHoldingRegistersAsync(1, 0, 1, CancellationToken.None)).Should().Equal(10);
    }
}
