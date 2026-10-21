namespace Bosun.Evals;

/// <summary>
/// What every results file must say about itself, so a number can never be mistaken for something it is not: whether a model produced
/// it, that cost is an estimate, and what an offline run does and does not prove.
/// </summary>
public static class EvalHonesty
{
    public const string PricingNote = "estimatedCostUsd is an ESTIMATE: tokens x the per-million-token prices in apps/backend/src/Bosun.Api/llm-pricing.json (list prices, not a bill; free-tier calls cost nothing).";

    public const string ScannedEchoNote = "OFFLINE: the scripted reader answers image pages from the ground truth, so the scanned-page F1 is an echo of the truth. It does not test page rendering or the vision model.";

    public const string OfflineScope = "OFFLINE: a scripted, faithful reader stands in for the model. These results prove the plumbing around the model (triage, reconstruction, normalisation, grounding, rules, confidence, flags) and say NOTHING about model accuracy.";

    public static void PrintOfflineBanner(string suite)
    {
        var line = new string('=', 100);
        Console.WriteLine(line);
        Console.WriteLine($" --offline  suite {suite}: no model, no network, no spend.");
        Console.WriteLine(" " + OfflineScope);
        Console.WriteLine(line);
    }
}
