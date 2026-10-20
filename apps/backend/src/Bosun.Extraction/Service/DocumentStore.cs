using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Bosun.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Bosun.Extraction;

/// <summary>Where uploaded documents live. The stored name is generated here; nothing a client sent ever becomes part of a path.</summary>
public interface IDocumentStore
{
    /// <summary>Writes the bytes under a fresh generated name and returns the name.</summary>
    Task<string> SaveAsync(byte[] bytes, CancellationToken ct);

    Task<byte[]> ReadAsync(string storedName, CancellationToken ct);

    void Delete(string storedName);
}

public sealed partial class FileDocumentStore : IDocumentStore
{
    private readonly string root;

    public FileDocumentStore(IOptions<ExtractionOptions> options, IConfiguration configuration)
    {
        root = Resolve(options.Value.StoragePath, configuration);
    }

    public string Root => root;

    /// <summary>Configured path, else a "documents" directory next to the SQLite file, else under the repo root's data directory.</summary>
    private static string Resolve(string? configured, IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured, configuration["Bosun:RepoRoot"] ?? Directory.GetCurrentDirectory());
        }

        var connection = configuration["Database:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(connection))
        {
            var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connection };
            var source = builder.TryGetValue("Data Source", out var value) ? value?.ToString() : null;
            if (!string.IsNullOrEmpty(source) && source != ":memory:" && !source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, "documents");
            }
        }

        return Path.Combine(configuration["Bosun:RepoRoot"] ?? Directory.GetCurrentDirectory(), "data", "documents");
    }

    [GeneratedRegex(@"^[0-9a-f]{32}\.pdf$", RegexOptions.CultureInvariant)]
    private static partial Regex StoredNameShape();

    public async Task<string> SaveAsync(byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var name = Guid.NewGuid().ToString("N") + ".pdf";
        await File.WriteAllBytesAsync(PathOf(name), bytes, ct);
        return name;
    }

    public async Task<byte[]> ReadAsync(string storedName, CancellationToken ct)
    {
        var path = PathOf(storedName);
        if (!File.Exists(path))
        {
            throw new AppException(ErrorKind.NotFound, "document_file_missing", "The stored file of this document is missing on the server.");
        }

        return await File.ReadAllBytesAsync(path, ct);
    }

    public void Delete(string storedName)
    {
        try
        {
            File.Delete(PathOf(storedName));
        }
        catch (IOException)
        {
            // best effort: an orphaned file is harmless
        }
    }

    private string PathOf(string storedName)
    {
        // Defence in depth: only names this class generated can ever reach the file system.
        if (!StoredNameShape().IsMatch(storedName))
        {
            throw new ArgumentException("Not a stored document name.", nameof(storedName));
        }

        return Path.Combine(root, storedName);
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
