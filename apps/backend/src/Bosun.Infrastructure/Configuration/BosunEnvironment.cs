using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace Bosun.Infrastructure.Configuration;

/// <summary>Locates the repository root (the directory containing <c>Bosun.sln</c>), independent of the process cwd.</summary>
public static class RepoRoot
{
    public const string Marker = "Bosun.sln";

    /// <summary>Walks up from each start directory until <see cref="Marker"/> is found; null when none contains it.</summary>
    public static string? TryFind(params string?[] startDirectories)
    {
        foreach (var start in startDirectories)
        {
            for (var dir = start is null ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, Marker)))
                {
                    return dir.FullName;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The repo root as seen from the binary's folder first (so <c>dotnet run</c>, <c>dotnet test</c> and
    /// <c>dotnet ef</c> agree), then the cwd; falls back to the cwd when running outside a checkout.
    /// </summary>
    public static string Resolve(string? startDirectory = null) =>
        TryFind(startDirectory, AppContext.BaseDirectory, Directory.GetCurrentDirectory()) ?? Directory.GetCurrentDirectory();
}

/// <summary>Minimal <c>.env</c> parser (KEY=VALUE, # comments, optional quotes).</summary>
public static class DotEnv
{
    public static Dictionary<string, string> Parse(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            values[key] = value;
        }

        return values;
    }
}

/// <summary>
/// Maps the single root <c>.env</c> (plus real environment variables, which win) onto configuration keys, so the
/// rest of the code never reads <c>DB_CONNECTION</c> and friends directly. Added as the lowest-priority source:
/// appsettings, command-line args and test overrides all beat it.
/// </summary>
public static class BosunEnvironment
{
    public const string DefaultAdminPassword = "admin123";

    /// <summary>Adds the .env mapping as the highest-priority source (design-time tools, tests).</summary>
    public static IConfigurationBuilder AddBosunEnv(this IConfigurationBuilder builder, string? startDirectory = null) =>
        builder.Add(CreateSource(startDirectory));

    /// <summary>
    /// Adds the .env mapping as the LOWEST-priority source, so appsettings, real configuration providers and
    /// command-line arguments (and test overrides added later) all beat it.
    /// </summary>
    public static ConfigurationManager AddBosunEnvAsDefaults(this ConfigurationManager configuration, string? startDirectory = null)
    {
        ((IConfigurationBuilder)configuration).Sources.Insert(0, CreateSource(startDirectory));
        return configuration;
    }

    public static IConfigurationSource CreateSource(string? startDirectory = null)
    {
        var root = RepoRoot.Resolve(startDirectory);
        var file = DotEnv.Parse(Path.Combine(root, ".env"));

        string? Get(string key)
        {
            // An empty value ("ADMIN_PASSWORD=") means "unset" everywhere, so a blank line can never become a blank credential.
            var fromEnv = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv;
            }

            return file.TryGetValue(key, out var fromFile) && !string.IsNullOrWhiteSpace(fromFile) ? fromFile : null;
        }

        var map = new Dictionary<string, string?>
        {
            ["Bosun:RepoRoot"] = root,
            ["Server:Port"] = Get("BACKEND_PORT") ?? "5480",
            ["Database:Provider"] = (Get("DB_PROVIDER") ?? "sqlite").ToLowerInvariant(),
            ["Database:ConnectionString"] = ResolveConnection(Get("DB_CONNECTION") ?? "Data Source=data/bosun.db", Get("DB_PROVIDER") ?? "sqlite", root),
            ["Auth:AdminUsername"] = Get("ADMIN_USERNAME") ?? "admin",
            ["Auth:AdminPassword"] = Get("ADMIN_PASSWORD") ?? DefaultAdminPassword,

            // LLM + embeddings (plan s10 overridden by TODO.md): provider/model names come from the env, never from code.
            // Keys are mapped but never printed or logged anywhere.
            ["Llm:Provider"] = Get("LLM_PROVIDER") ?? "gemini",
            ["Llm:Model"] = Get("LLM_MODEL"),
            ["Llm:ApiKey"] = Get("GEMINI_LLM_API_KEY"),
            ["Embedding:Provider"] = Get("EMBEDDING_PROVIDER") ?? "gemini",
            ["Embedding:Model"] = Get("EMBEDDING_MODEL"),
            ["Embedding:Dimensions"] = Get("EMBEDDING_DIMS") ?? "768",
            ["Embedding:ApiKey"] = Get("GEMINI_EMBEDDING_API_KEY"),

            // Optional second user holding the "approver" role (SIL/ESD second approval). Development gets a documented default.
            ["Auth:ApproverUsername"] = Get("APPROVER_USERNAME"),
            ["Auth:ApproverPassword"] = Get("APPROVER_PASSWORD"),

            // Live monitoring: where the simulator (or a real PLC / NMEA talker) listens. Loopback by default.
            ["Monitoring:Modbus:Port"] = Get("SIM_MODBUS_PORT") ?? "5483",
            ["Monitoring:Nmea:Port"] = Get("SIM_NMEA_PORT") ?? "5484",
        };

        return new MemoryConfigurationSource { InitialData = map };
    }

    /// <summary>For SQLite, rewrites a relative <c>Data Source</c> to an absolute path under the repo root.</summary>
    public static string ResolveConnection(string connection, string provider, string root)
    {
        if (!provider.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return connection;
        }

        var builder = new SqliteConnectionStringBuilder(connection);
        var source = builder.DataSource;
        if (!string.IsNullOrEmpty(source) && source != ":memory:" && !source.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && !Path.IsPathRooted(source))
        {
            builder.DataSource = Path.GetFullPath(Path.Combine(root, source));
        }

        return builder.ToString();
    }
}
