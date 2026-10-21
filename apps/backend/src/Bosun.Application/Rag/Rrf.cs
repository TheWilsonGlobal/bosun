namespace Bosun.Application.Rag;

/// <summary>Reciprocal rank fusion: score(d) = sum over lists of weight / (k + rank), ranks starting at 1.</summary>
public static class Rrf
{
    public static IReadOnlyList<(int Id, double Score)> Fuse(IReadOnlyList<IReadOnlyList<int>> rankedLists, IReadOnlyList<double> weights, int k)
    {
        var scores = new Dictionary<int, double>();
        var firstSeen = new Dictionary<int, int>();
        for (var list = 0; list < rankedLists.Count; list++)
        {
            for (var rank = 0; rank < rankedLists[list].Count; rank++)
            {
                var id = rankedLists[list][rank];
                scores[id] = scores.GetValueOrDefault(id) + (weights[list] / (k + rank + 1));
                firstSeen.TryAdd(id, firstSeen.Count);
            }
        }

        // Ties (the same score from different lists) break by first appearance, so the result is deterministic.
        return scores.OrderByDescending(kv => kv.Value).ThenBy(kv => firstSeen[kv.Key]).Select(kv => (kv.Key, kv.Value)).ToList();
    }
}
