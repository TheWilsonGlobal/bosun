namespace Bosun.Simulator;

/// <summary>
/// Reads settings from real environment variables first, then from the single root <c>.env</c> (found by walking up to
/// <c>Bosun.sln</c>). A blank value means "unset". Values are never printed.
/// </summary>
public sealed class SimulatorEnvironment
{
    private readonly Dictionary<string, string> file = [];

    public SimulatorEnvironment()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Bosun.sln")))
            {
                continue;
            }

            var env = Path.Combine(dir.FullName, ".env");
            if (File.Exists(env))
            {
                foreach (var raw in File.ReadAllLines(env))
                {
                    var line = raw.Trim();
                    var eq = line.IndexOf('=');
                    if (line.Length == 0 || line[0] == '#' || eq <= 0)
                    {
                        continue;
                    }

                    file[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"', '\'');
                }
            }

            break;
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
