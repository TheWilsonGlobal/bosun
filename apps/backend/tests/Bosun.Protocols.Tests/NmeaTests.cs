using System.Text;
using Bosun.Protocols.Nmea;

namespace Bosun.Protocols.Tests;

public class NmeaTests
{
    // Documented example (checksum 6A): position 48 07.038 N, 11 31.000 E, 022.4 kn, course 084.4, 23 Mar 1994 12:35:19 UTC.
    private const string DocRmc = "$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*6A";
    private const string DocGga = "$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47";
    private const string Hdt = "$HEHDT,274.07,T*19";

    [Fact]
    public void Checksum_is_the_xor_of_the_body()
    {
        NmeaParser.Checksum("GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W").Should().Be(0x6A);
        NmeaParser.Checksum("GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,").Should().Be(0x47);
        NmeaParser.Build("HEHDT,274.07,T").Should().Be(Hdt);
    }

    [Fact]
    public void Parses_the_documented_rmc_sentence()
    {
        var sentence = NmeaParser.Parse(DocRmc);
        sentence.Talker.Should().Be("GP");
        sentence.Type.Should().Be("RMC");

        var rmc = NmeaParser.ParseRmc(sentence);
        rmc.Valid.Should().BeTrue();
        rmc.Latitude.Should().BeApproximately(48.1173, 1e-4); // 48 deg + 7.038 min
        rmc.Longitude.Should().BeApproximately(11.516667, 1e-6);
        rmc.SpeedKnots.Should().Be(22.4);
        rmc.CourseTrueDeg.Should().Be(84.4);
        rmc.TimeUtc.Should().Be(new DateTime(1994, 3, 23, 12, 35, 19, DateTimeKind.Utc));
    }

    [Fact]
    public void Parses_hdt()
    {
        NmeaParser.ParseHdt(NmeaParser.Parse(Hdt)).HeadingTrueDeg.Should().Be(274.07);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("  \r\n")]
    [InlineData("")]
    public void Line_endings_and_trailing_whitespace_are_tolerated(string ending)
    {
        NmeaParser.Parse(DocRmc + ending).Type.Should().Be("RMC");
    }

    [Fact]
    public void Lowercase_hex_checksum_is_accepted()
    {
        NmeaParser.Parse("$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*6a").Type.Should().Be("RMC");
    }

    [Fact]
    public void A_wrong_checksum_is_rejected()
    {
        var act = () => NmeaParser.Parse(DocRmc.Replace("*6A", "*6B"));
        act.Should().Throw<NmeaException>().Which.Error.Should().Be(NmeaError.BadChecksum);
    }

    [Fact]
    public void A_corrupted_body_fails_the_checksum()
    {
        var act = () => NmeaParser.Parse(DocRmc.Replace("022.4", "022.5"));
        act.Should().Throw<NmeaException>().Which.Error.Should().Be(NmeaError.BadChecksum);
    }

    [Theory]
    [InlineData("$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W")] // checksum missing
    [InlineData("$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*")] // star, no digits
    [InlineData("$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*6")] // one digit
    [InlineData("$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,08")] // cut off mid-field
    [InlineData("$GPRMC,123519,A,4807.038,N,01131")]
    public void Truncated_sentences_are_rejected(string line)
    {
        var act = () => NmeaParser.Parse(line);
        act.Should().Throw<NmeaException>().Which.Error.Should().Be(NmeaError.Truncated);
    }

    [Theory]
    [InlineData("GPRMC,123519,A*6A", NmeaError.MissingStart)]
    [InlineData("", NmeaError.Empty)]
    [InlineData("   \r\n", NmeaError.Empty)]
    [InlineData("$GPRMC,1*ZZ", NmeaError.Malformed)]
    public void Other_malformed_lines_are_classified(string line, NmeaError expected)
    {
        var act = () => NmeaParser.Parse(line);
        act.Should().Throw<NmeaException>().Which.Error.Should().Be(expected);
    }

    [Fact]
    public void An_overlong_line_is_rejected()
    {
        var act = () => NmeaParser.Parse("$" + new string('A', 300) + "*00");
        act.Should().Throw<NmeaException>().Which.Error.Should().Be(NmeaError.TooLong);
    }

    [Fact]
    public void A_bad_address_field_is_malformed_even_with_a_valid_checksum()
    {
        var act = () => NmeaParser.Parse(NmeaParser.Build("gprmc,1"));
        act.Should().Throw<NmeaException>().Which.Error.Should().Be(NmeaError.Malformed);
    }

    [Fact]
    public void Rmc_without_a_fix_has_a_false_valid_flag_and_no_position()
    {
        var rmc = NmeaParser.ParseRmc(NmeaParser.Parse(NmeaParser.Build("GPRMC,,V,,,,,,,,,N")));
        rmc.Valid.Should().BeFalse();
        rmc.Latitude.Should().BeNull();
        rmc.Longitude.Should().BeNull();
        rmc.TimeUtc.Should().BeNull();
    }

    [Fact]
    public void South_and_west_are_negative()
    {
        var rmc = NmeaParser.ParseRmc(NmeaParser.Parse(NmeaParser.Build("GNRMC,010203.50,A,3351.000,S,15112.000,W,5.0,10.0,010125,,")));
        rmc.Latitude.Should().BeApproximately(-33.85, 1e-9);
        rmc.Longitude.Should().BeApproximately(-151.2, 1e-9);
        rmc.TimeUtc.Should().Be(new DateTime(2025, 1, 1, 1, 2, 3, DateTimeKind.Utc).AddMilliseconds(500));
    }

    [Theory]
    [InlineData("GPRMC,123519,A,4807.038,X,01131.000,E,1,1,230394")] // bad hemisphere
    [InlineData("GPRMC,123519,A,4899.038,N,01131.000,E,1,1,230394")] // 99 minutes
    [InlineData("GPRMC,123519,A,4807.038,N,01131.000,E,fast,1,230394")] // speed not a number
    [InlineData("GPRMC,251519,A,4807.038,N,01131.000,E,1,1,230394")] // 25 hours
    [InlineData("GPRMC,123519,A,4807.038,N,01131.000,E,1,1,310294")] // 31 February
    [InlineData("GPRMC,123519,A,4807.038,N")] // too few fields
    public void Bad_rmc_fields_are_rejected(string body)
    {
        var sentence = NmeaParser.Parse(NmeaParser.Build(body));
        var act = () => NmeaParser.ParseRmc(sentence);
        act.Should().Throw<NmeaException>().Which.Error.Should().Be(NmeaError.Malformed);
    }

    [Theory]
    [InlineData("HEHDT,,T")]
    [InlineData("HEHDT,abc,T")]
    [InlineData("HEHDT,400.0,T")]
    public void Bad_hdt_heading_is_rejected(string body)
    {
        var sentence = NmeaParser.Parse(NmeaParser.Build(body));
        var act = () => NmeaParser.ParseHdt(sentence);
        act.Should().Throw<NmeaException>();
    }

    [Fact]
    public void Wrong_sentence_type_is_rejected_by_the_typed_parsers()
    {
        var gga = NmeaParser.Parse(DocGga);
        var act = () => NmeaParser.ParseRmc(gga);
        act.Should().Throw<NmeaException>();
        var act2 = () => NmeaParser.ParseHdt(gga);
        act2.Should().Throw<NmeaException>();
    }

    [Fact]
    public void TryParse_reports_the_error_without_throwing()
    {
        NmeaParser.TryParse("$GPRMC,1*00", out var sentence, out var error).Should().BeFalse();
        sentence.Should().BeNull();
        error.Should().Be(NmeaError.BadChecksum);
        NmeaParser.TryParse(Hdt, out sentence, out error).Should().BeTrue();
        sentence!.Type.Should().Be("HDT");
        error.Should().BeNull();
    }

    [Fact]
    public async Task Line_reader_reassembles_fragments_and_both_line_endings()
    {
        var stream = new ScriptedStream()
            .Feed(Encoding.ASCII.GetBytes("$GPRMC,123519,A,4807.0"))
            .Feed(Encoding.ASCII.GetBytes("38,N,01131.000,E,022.4,084.4,230394,003.1,W*6A\r"))
            .Feed(Encoding.ASCII.GetBytes("\n$HEHDT,274.07,T*19\n\n$GPG"))
            .ThenEnd();
        var reader = new NmeaLineReader(stream);

        (await reader.ReadLineAsync(CancellationToken.None)).Should().Be(DocRmc);
        (await reader.ReadLineAsync(CancellationToken.None)).Should().Be(Hdt);
        (await reader.ReadLineAsync(CancellationToken.None)).Should().BeNull("the trailing partial line is dropped at end of stream");
    }

    [Fact]
    public async Task Line_reader_drops_an_overlong_line_and_recovers()
    {
        var junk = new string('x', 700);
        var stream = new ScriptedStream().Feed(Encoding.ASCII.GetBytes($"{junk}\n{Hdt}\r\n")).ThenEnd();
        var reader = new NmeaLineReader(stream, maxLineBytes: 128);

        (await reader.ReadLineAsync(CancellationToken.None)).Should().Be(Hdt);
        reader.Overflows.Should().Be(1);
    }
}
