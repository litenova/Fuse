using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fuse.Collection;

/// <summary>
///     Reads the repository-scoped Fuse workspace configuration.
/// </summary>
/// <remarks>
///     Configuration is intentionally small. Indexing and workspace discovery need only a pinned workspace and
///     extra ignored directories, so legacy emission and cache settings are not accepted here.
/// </remarks>
public static class WorkspaceConfiguration
{
    /// <summary>The only supported repository configuration file.</summary>
    public const string FileName = "fuse.json";

    /// <summary>The schema URL written by <c>fuse init</c>.</summary>
    public const string SchemaUrl = "https://fuse.codes/schema/v4.4/fuse.schema.json";

    private static readonly HashSet<string> KnownProperties = new(StringComparer.Ordinal)
    {
        "$schema",
        "workspace",
        "ignore",
    };

    /// <summary>
    ///     Loads configuration from the repository root.
    /// </summary>
    /// <param name="rootDirectory">The repository root containing the optional configuration file.</param>
    /// <returns>The parsed options, warnings, and an error when the file is invalid.</returns>
    public static WorkspaceConfigurationLoadResult Load(string rootDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
            return new WorkspaceConfigurationLoadResult(FuseWorkspaceOptions.Empty, null, [], null);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Invalid(path, "$: expected an object");

            var warnings = new List<string>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!KnownProperties.Contains(property.Name))
                    warnings.Add($"fuse.json: unknown property '{property.Name}' is ignored.");
            }

            if (!TryValidatePropertyTypes(document.RootElement, out var validationError))
                return Invalid(path, validationError!);

            var options = JsonSerializer.Deserialize(
                document.RootElement.GetRawText(),
                WorkspaceConfigurationJsonContext.Default.FuseWorkspaceOptions)
                ?? FuseWorkspaceOptions.Empty;
            options = options with { Ignore = options.Ignore ?? [] };

            if (!string.IsNullOrWhiteSpace(options.Workspace))
            {
                var resolved = ResolveWorkspacePath(root, options.Workspace);
                if (resolved is null)
                    return Invalid(path, "workspace: must name an existing .sln, .slnx, .slnf, or .csproj below the repository root");

                options = options with { Workspace = Path.GetRelativePath(root, resolved) };
            }

            return new WorkspaceConfigurationLoadResult(options, path, warnings, null);
        }
        catch (JsonException ex)
        {
            return Invalid(path, $"invalid JSON: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Invalid(path, $"could not read configuration: {ex.Message}");
        }
    }

    /// <summary>
    ///     Loads configuration and raises an actionable exception when the file is invalid.
    /// </summary>
    /// <param name="rootDirectory">The repository root containing the optional configuration file.</param>
    /// <returns>The parsed options.</returns>
    public static FuseWorkspaceOptions LoadOrThrow(string rootDirectory)
    {
        var result = Load(rootDirectory);
        if (result.Error is not null)
            throw new WorkspaceConfigurationException(result.Error);
        return result.Options;
    }

    /// <summary>
    ///     Resolves a configured workspace path without allowing an escape from the repository root.
    /// </summary>
    /// <param name="rootDirectory">The repository root.</param>
    /// <param name="configuredPath">The configured relative or absolute path.</param>
    /// <returns>The absolute existing path, or <see langword="null" /> when it is invalid.</returns>
    public static string? ResolveWorkspacePath(string rootDirectory, string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var candidate = Path.GetFullPath(
            Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(root, configuredPath));
        if (!IsWithinRoot(root, candidate))
            return null;

        var extension = Path.GetExtension(candidate);
        if (!extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".slnf", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            return null;

        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    ///     Serializes the starter configuration written by <c>fuse init</c>.
    /// </summary>
    /// <param name="workspace">An optional repository-relative workspace path.</param>
    /// <returns>Indented JSON followed by the platform newline.</returns>
    public static string CreateTemplate(string? workspace)
    {
        var options = new FuseWorkspaceOptions(SchemaUrl, workspace, []);
        return JsonSerializer.Serialize(options, WorkspaceConfigurationJsonContext.Default.FuseWorkspaceOptions)
            + Environment.NewLine;
    }

    private static WorkspaceConfigurationLoadResult Invalid(string path, string error) =>
        new(FuseWorkspaceOptions.Empty, path, [], $"{path}: {error}");

    private static bool TryValidatePropertyTypes(JsonElement root, out string? error)
    {
        error = null;
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "$schema" when property.Value.ValueKind is not JsonValueKind.String and not JsonValueKind.Null:
                    error = "$schema: expected a string";
                    return false;
                case "workspace" when property.Value.ValueKind is not JsonValueKind.String and not JsonValueKind.Null:
                    error = "workspace: expected a string";
                    return false;
                case "ignore" when property.Value.ValueKind != JsonValueKind.Array:
                    error = "ignore: expected an array of strings";
                    return false;
                case "ignore":
                    foreach (var value in property.Value.EnumerateArray())
                    {
                        if (value.ValueKind != JsonValueKind.String)
                        {
                            error = "ignore: expected an array of strings";
                            return false;
                        }
                    }
                    break;
            }
        }

        return true;
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
///     The supported repository configuration fields.
/// </summary>
/// <param name="Schema">The optional schema URL.</param>
/// <param name="Workspace">The optional repository-relative solution, solution filter, or project path.</param>
/// <param name="Ignore">Extra directory names excluded from scanning.</param>
public sealed record FuseWorkspaceOptions(
    [property: JsonPropertyName("$schema")] string? Schema,
    [property: JsonPropertyName("workspace")] string? Workspace,
    [property: JsonPropertyName("ignore")] IReadOnlyList<string> Ignore)
{
    /// <summary>The default options when no configuration file exists.</summary>
    public static FuseWorkspaceOptions Empty { get; } = new(null, null, []);
}

/// <summary>
///     The result of loading repository configuration.
/// </summary>
/// <param name="Options">Parsed options, or empty options on error.</param>
/// <param name="Path">The configuration path when it exists.</param>
/// <param name="Warnings">Unknown-property warnings.</param>
/// <param name="Error">The actionable validation error, when present.</param>
public sealed record WorkspaceConfigurationLoadResult(
    FuseWorkspaceOptions Options,
    string? Path,
    IReadOnlyList<string> Warnings,
    string? Error);

/// <summary>
///     Raised when repository configuration cannot drive a safe workspace operation.
/// </summary>
public sealed class WorkspaceConfigurationException(string message) : InvalidOperationException(message);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FuseWorkspaceOptions))]
internal partial class WorkspaceConfigurationJsonContext : JsonSerializerContext;
