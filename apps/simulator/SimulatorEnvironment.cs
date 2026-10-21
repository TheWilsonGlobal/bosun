namespace Bosun.Simulator;

/// <summary>
/// Reads settings from real environment variables first, then from the single root <c>.env</c> (found by walking up to
/// <c>Bosun.sln</c>; only keys starting with <c>SIM_</c> are read). A blank value means "unset". Values are never printed.
/// </summary>
public sealed class SimulatorEnvironment
{
    public const string Prefix = "SIM_";


    private readonly Dictionary<string, string> file = [];

    public SimulatorEnvironment()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Bosun.sln")))
            {
                Load(Path.Combine(dir.FullName, ".env"));
                break;
            }
        }
    }

    /// <summary>Reads one specific env file (tests).</summary>
    public SimulatorEnvironment(string envFile) => Load(envFile);

    private void Load(string env)
    {
        if (!File.Exists(env))
        {
            return;
        }

        foreach (var raw in File.ReadAllLines(env))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (line.Length == 0 || line[0] == '#' || eq <= 0)
            {
                continue;
            }

            // Only the simulator's own SIM_* settings are read: the root .env also holds the API keys and the admin password,
            // and this process has no use for them, so they are never even loaded into its memory.
            var key = line[..eq].Trim();
            if (key.StartsWith(Prefix, StringComparison.Ordinal))
            {
                file[key] = line[(eq + 1)..].Trim().Trim('"', '\'');
            }
        }
    }

    public string? Get(string key)
    {
        var fromEnv = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        return file.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }
}
