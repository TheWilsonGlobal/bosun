using System.Diagnostics;
using Xunit.Abstractions;

namespace Bosun.Api.IntegrationTests;

/// <summary>
/// Plan section 11 Phase 1: GET /api/projects/1/tags?filter=ME-EXH must answer in under 50 ms, warm.
/// The contract stays 50 ms. To keep the test from flaking on a busy machine (another test class, GC, antivirus), it
/// warms up, takes many samples, asserts on the MEDIAN and p95 of the whole run (never the max), and gives a run that
/// misses the budget up to <see cref="Attempts"/> chances: a real regression misses every time, a noisy neighbour does not.
/// </summary>
public class PerformanceTests(TestApp app, ITestOutputHelper output) : IClassFixture<TestApp>
{
    private const int Attempts = 3;
    private const int WarmUp = 50;
    private const int Samples = 300;
    private const double BudgetMs = 50;

    [Theory]
    [InlineData("/api/projects/1/tags?filter=ME-EXH")]
    [InlineData("/api/projects/1/tags?filter=ME-EXH&pageSize=100&sort=tagName")]
    [InlineData("/api/projects/1/tags?filter=exh&system=ME&hasAlarm=false")]
    [InlineData("/api/projects/1/tags?page=1&pageSize=100&sort=rangeHigh&order=desc")]
    public async Task Tag_grid_requests_are_under_50ms_warm(string url)
    {
        var client = app.CreateClient();
        double median = 0;
        double p95 = 0;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            for (var i = 0; i < WarmUp; i++)
            {
                (await client.GetAsync(url)).EnsureSuccessStatusCode();
            }

            var timings = new List<double>(Samples);
            for (var i = 0; i < Samples; i++)
            {
                var watch = Stopwatch.StartNew();
                var response = await client.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();
                watch.Stop();
                response.EnsureSuccessStatusCode();
                body.Length.Should().BeGreaterThan(10);
                timings.Add(watch.Elapsed.TotalMilliseconds);
            }

            timings.Sort();
            median = timings[Samples / 2];
            p95 = timings[(int)(Samples * 0.95)];
            output.WriteLine($"PERF {url}  attempt {attempt}/{Attempts}  n={Samples} (after {WarmUp} warm-up)  median={median:F2} ms  p95={p95:F2} ms  max={timings[^1]:F2} ms");
            if (median < BudgetMs && p95 < BudgetMs)
            {
                return;
            }
        }

        median.Should().BeLessThan(BudgetMs, $"all {Attempts} attempts missed the budget");
        p95.Should().BeLessThan(BudgetMs, $"all {Attempts} attempts missed the budget");
    }
}
