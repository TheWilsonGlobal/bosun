namespace Bosun.Protocols.Tests;

public class ReconnectPolicyTests
{
    [Fact]
    public void Delays_double_up_to_the_cap_when_there_is_no_jitter_deviation()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(8), factor: 2, jitter: 0.25, random: () => 0.5);

        var delays = Enumerable.Range(0, 7).Select(_ => policy.NextDelay().TotalMilliseconds).ToArray();

        delays.Should().Equal(500, 1000, 2000, 4000, 8000, 8000, 8000);
        policy.Attempts.Should().Be(7);
    }

    [Fact]
    public void Jitter_stays_within_the_band_and_never_exceeds_the_cap()
    {
        var low = new ReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), jitter: 0.25, random: () => 0.0);
        var high = new ReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), jitter: 0.25, random: () => 0.999999);

        low.NextDelay().TotalMilliseconds.Should().BeApproximately(750, 0.001);
        high.NextDelay().TotalMilliseconds.Should().BeApproximately(1250, 0.01);

        for (var i = 0; i < 20; i++)
        {
            low.NextDelay();
            high.NextDelay().Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void Random_jitter_varies_between_instances()
    {
        var seen = Enumerable.Range(0, 20).Select(_ => new ReconnectPolicy().NextDelay().TotalMilliseconds).Distinct().Count();
        seen.Should().BeGreaterThan(5);
    }

    [Fact]
    public void Reset_starts_the_sequence_over()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5), jitter: 0, random: () => 0.5);
        policy.NextDelay();
        policy.NextDelay();
        policy.NextDelay().TotalMilliseconds.Should().Be(400);

        policy.Reset();

        policy.Attempts.Should().Be(0);
        policy.NextDelay().TotalMilliseconds.Should().Be(100);
    }

    [Fact]
    public void A_huge_number_of_failures_does_not_overflow()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(15), jitter: 0, random: () => 0.5);
        TimeSpan last = default;
        for (var i = 0; i < 5000; i++)
        {
            last = policy.NextDelay();
        }

        last.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Theory]
    [InlineData(0, 1000, 1.0, 0.1)]
    [InlineData(2000, 1000, 1.0, 0.1)]
    [InlineData(100, 1000, 0.5, 0.1)]
    [InlineData(100, 1000, 2.0, 1.5)]
    public void Invalid_settings_are_refused(int initialMs, int maxMs, double factor, double jitter)
    {
        var act = () => new ReconnectPolicy(TimeSpan.FromMilliseconds(initialMs), TimeSpan.FromMilliseconds(maxMs), factor, jitter);
        act.Should().Throw<ArgumentException>();
    }
}
