using System.Globalization;
using System.Text;

namespace Bosun.Protocols.Nmea;

/// <summary>Why a line was rejected. Stable names: they end up in logs and test assertions.</summary>
public enum NmeaError
{
    Empty,
    MissingStart,
    Truncated,
    BadChecksum,
    TooLong,
    Malformed,
}

public sealed class NmeaException(NmeaError error, string message) : Exception(message)
{
    public NmeaError Error { get; } = error;
}

/// <summary>A checksum-valid sentence split into fields. <see cref="Talker"/> is the 2 letter source ("GP", "HE"), <see cref="Type"/> the 3 letter formatter ("RMC", "HDT").</summary>
public sealed record NmeaSentence(string Talker, string Type, IReadOnlyList<string> Fields, string Raw);

/// <summary>Recommended minimum navigation data. Latitude/longitude are signed decimal degrees (south and west negative).</summary>
public sealed record NmeaRmc(
    bool Valid,
    DateTime? TimeUtc,
    double? Latitude,
    double? Longitude,
    double? SpeedKnots,
    double? CourseTrueDeg);

/// <summary>Heading, true (degrees).</summary>
public sealed record NmeaHdt(double HeadingTrueDeg);

/// <summary>
/// NMEA 0183 parsing. A sentence is <c>$</c> + body + <c>*</c> + two hex digits (XOR of every byte of the body) + CR LF.
/// <code>
/// $GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*6A     (documented example: checksum 6A)
/// $GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47        (documented example: checksum 47)
/// </code>
/// A line without a checksum field is treated as truncated: a talker that cut off mid-sentence is indistinguishable from
/// one that never sends checksums, and Bosun's talkers always send them.
/// </summary>
public static class NmeaParser
{
    /// <summary>The standard limit is 82 characters including the CR LF; a little slack is allowed for non-conforming talkers.</summary>
    public const int MaxLineLength = 120;

    public static byte Checksum(ReadOnlySpan<char> body)
    {
        byte value = 0;
        foreach (var ch in body)
        {
            value ^= (byte)ch;
        }

        return value;
    }

    /// <summary>Builds a complete sentence (no CR LF) from a body such as <c>HEHDT,274.07,T</c>.</summary>
    public static string Build(string body) => $"${body}*{Checksum(body):X2}";

    public static NmeaSentence Parse(string line)
    {
        var text = line.Trim(); // tolerates CR LF, LF, stray spaces
        if (text.Length == 0)
        {
            throw new NmeaException(NmeaError.Empty, "Empty line.");
        }

        if (text.Length > MaxLineLength)
        {
            throw new NmeaException(NmeaError.TooLong, $"Line is {text.Length} characters (limit {MaxLineLength}).");
        }

        if (text[0] != '$')
        {
            throw new NmeaException(NmeaError.MissingStart, "A sentence starts with '$'.");
        }

        var star = text.LastIndexOf('*');
        if (star < 0 || star + 3 != text.Length)
        {
            throw new NmeaException(NmeaError.Truncated, "Missing or incomplete '*hh' checksum.");
        }

        var body = text[1..star];
        if (!byte.TryParse(text.AsSpan(star + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var expected))
        {
            throw new NmeaException(NmeaError.Malformed, "The checksum is not two hex digits.");
        }

        if (Checksum(body) != expected)
        {
            throw new NmeaException(NmeaError.BadChecksum, $"Checksum {expected:X2} does not match the body ({Checksum(body):X2}).");
        }

        var fields = body.Split(',');
        var address = fields[0];
        if (address.Length != 5 || !address.All(char.IsAsciiLetterUpper))
        {
            throw new NmeaException(NmeaError.Malformed, $"Address field '{address}' is not a 2 letter talker + 3 letter type.");
        }

        return new NmeaSentence(address[..2], address[2..], fields.Skip(1).ToArray(), text);
    }

    public static bool TryParse(string line, out NmeaSentence? sentence, out NmeaError? error)
    {
        try
        {
            sentence = Parse(line);
            error = null;
            return true;
        }
        catch (NmeaException ex)
        {
            sentence = null;
            error = ex.Error;
            return false;
        }
    }

    /// <summary>Decodes an RMC sentence of any talker (GP, GN, GL ...). Throws <see cref="NmeaException"/> when it is not RMC or a field is malformed.</summary>
    public static NmeaRmc ParseRmc(NmeaSentence sentence)
    {
        if (sentence.Type != "RMC")
        {
            throw new NmeaException(NmeaError.Malformed, $"Not an RMC sentence ({sentence.Talker}{sentence.Type}).");
        }

        var f = sentence.Fields;
        if (f.Count < 9)
        {
            throw new NmeaException(NmeaError.Malformed, $"RMC needs at least 9 fields, got {f.Count}.");
        }

        var valid = f[1] == "A";
        double? lat = Coordinate(f[2], f[3], 2, 'N', 'S');
        double? lon = Coordinate(f[4], f[5], 3, 'E', 'W');
        return new NmeaRmc(valid, TimeOf(f[0], f[8]), lat, lon, Number(f[6]), Number(f[7]));
    }

    public static NmeaHdt ParseHdt(NmeaSentence sentence)
    {
        if (sentence.Type != "HDT")
        {
            throw new NmeaException(NmeaError.Malformed, $"Not an HDT sentence ({sentence.Talker}{sentence.Type}).");
        }

        if (sentence.Fields.Count < 1 || Number(sentence.Fields[0]) is not { } heading || heading is < 0 or > 360)
        {
            throw new NmeaException(NmeaError.Malformed, "HDT needs a heading within 0..360.");
        }

        return new NmeaHdt(heading);
    }

    private static double? Number(string field)
    {
        if (field.Length == 0)
        {
            return null;
        }

        if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            throw new NmeaException(NmeaError.Malformed, $"'{field}' is not a number.");
        }

        return value;
    }

    /// <summary>ddmm.mmmm (latitude, 2 degree digits) or dddmm.mmmm (longitude, 3) + hemisphere letter.</summary>
    private static double? Coordinate(string value, string hemisphere, int degreeDigits, char positive, char negative)
    {
        if (value.Length == 0 && hemisphere.Length == 0)
        {
            return null;
        }

        if (value.Length < degreeDigits + 2 || hemisphere.Length != 1 || (hemisphere[0] != positive && hemisphere[0] != negative)
            || !double.TryParse(value[..degreeDigits], NumberStyles.None, CultureInfo.InvariantCulture, out var degrees)
            || !double.TryParse(value[degreeDigits..], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
            || minutes is < 0 or >= 60)
        {
            throw new NmeaException(NmeaError.Malformed, $"Bad coordinate '{value},{hemisphere}'.");
        }

        var result = degrees + (minutes / 60.0);
        return hemisphere[0] == negative ? -result : result;
    }

    /// <summary>hhmmss(.ss) + ddmmyy to UTC; null when either is empty.</summary>
    private static DateTime? TimeOf(string time, string date)
    {
        if (time.Length == 0 || date.Length == 0)
        {
            return null;
        }

        if (time.Length < 6 || date.Length != 6
            || !int.TryParse(time[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var hh)
            || !int.TryParse(time[2..4], NumberStyles.None, CultureInfo.InvariantCulture, out var mm)
            || !double.TryParse(time[4..], NumberStyles.Float, CultureInfo.InvariantCulture, out var ss)
            || !int.TryParse(date[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var day)
            || !int.TryParse(date[2..4], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(date[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var yy))
        {
            throw new NmeaException(NmeaError.Malformed, $"Bad time/date '{time}' '{date}'.");
        }

        var year = yy < 80 ? 2000 + yy : 1900 + yy; // GPS epoch is 1980
        try
        {
            return new DateTime(year, month, day, hh, mm, 0, DateTimeKind.Utc).AddSeconds(ss);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new NmeaException(NmeaError.Malformed, $"Impossible time/date '{time}' '{date}'.");
        }
    }
}

/// <summary>
/// Splits a byte stream into NMEA lines. Lines may arrive in fragments across reads; CR LF and bare LF both end a line.
/// A line that grows past the limit without ending is discarded and counted in <see cref="Overflows"/>.
/// </summary>
public sealed class NmeaLineReader(Stream stream, int maxLineBytes = 512)
{
    private readonly byte[] chunk = new byte[256];
    private readonly StringBuilder current = new();
    private readonly Queue<string> ready = new();
    private bool discarding;

    /// <summary>Number of over-long lines dropped so far.</summary>
    public int Overflows { get; private set; }

    /// <summary>Next complete line (without line terminator), or null at end of stream. A trailing partial line at EOF is dropped.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (ready.Count == 0)
        {
            var n = await stream.ReadAsync(chunk, cancellationToken);
            if (n == 0)
            {
                return null;
            }

            for (var i = 0; i < n; i++)
            {
                var b = chunk[i];
                if (b == (byte)'\n')
                {
                    if (!discarding && current.Length > 0)
                    {
                        ready.Enqueue(current.ToString().TrimEnd('\r'));
                    }

                    current.Clear();
                    discarding = false;
                }
                else if (!discarding)
                {
                    if (current.Length >= maxLineBytes)
                    {
                        discarding = true;
                        Overflows++;
                        current.Clear();
                    }
                    else
                    {
                        current.Append((char)b);
                    }
                }
            }
        }

        return ready.Dequeue();
    }
}
