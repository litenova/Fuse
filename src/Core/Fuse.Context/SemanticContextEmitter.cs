using System.Text;
using System.Text.Json;
using Fuse.Retrieval;
using Fuse.Scoping;

namespace Fuse.Context;

/// <summary>
///     Emits a rendered context as XML, Markdown, or JSON: the semantic manifest preamble followed by each
///     file's provenance and rendered body.
/// </summary>
/// <remarks>
///     The token budget is honored upstream by the plan packing, so the emitter writes exactly the files the
///     plan kept; redaction is applied by the renderer before this point. JSON uses a source-generated
///     serializer context, per the project's reflection-free serialization invariant.
/// </remarks>
public static class SemanticContextEmitter
{
    /// <summary>
    ///     Emits the rendered context in the requested format.
    /// </summary>
    /// <param name="plan">The context plan (for the manifest).</param>
    /// <param name="rendered">The rendered files.</param>
    /// <param name="format">The output format.</param>
    /// <param name="root">The workspace root, for the manifest.</param>
    /// <param name="changedSince">The git base ref for review plans, for the manifest.</param>
    /// <param name="unchangedPaths">Paths to emit as a reference (body omitted) because they were already sent unchanged in the session.</param>
    /// <param name="apiDeltaSection">The rendered public-API delta section (T2) for a review plan, or null to omit.</param>
    /// <param name="claimsSection">The rendered claim-ledger section for a review plan, or null to omit.</param>
    /// <returns>The emitted payload.</returns>
    public static string Emit(
        ContextPlan plan,
        RenderedContext rendered,
        ContextOutputFormat format,
        string? root = null,
        string? changedSince = null,
        IReadOnlyCollection<string>? unchangedPaths = null,
        string? apiDeltaSection = null,
        string? claimsSection = null)
    {
        var unchanged = unchangedPaths ?? [];
        return format switch
        {
            ContextOutputFormat.Markdown => EmitMarkdown(plan, rendered, root, changedSince, unchanged, apiDeltaSection, claimsSection),
            ContextOutputFormat.Json => EmitJson(plan, rendered, root, changedSince, unchanged, apiDeltaSection, claimsSection),
            _ => EmitXml(plan, rendered, root, changedSince, unchanged, apiDeltaSection, claimsSection),
        };
    }

    private static string EmitXml(ContextPlan plan, RenderedContext rendered, string? root, string? changedSince, IReadOnlyCollection<string> unchanged, string? apiDeltaSection, string? claimsSection = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!--");
        builder.Append(SemanticManifestBuilder.Build(plan, root, changedSince, apiDeltaSection, claimsSection));
        builder.AppendLine("-->");

        foreach (var file in rendered.Files)
        {
            if (unchanged.Contains(file.Path))
            {
                builder.AppendLine($"<file path=\"{EscapeAttribute(file.Path)}\" role=\"{file.Role}\" unchanged=\"true\" />");
                continue;
            }

            builder.AppendLine(
                $"<file path=\"{EscapeAttribute(file.Path)}\" role=\"{file.Role}\" tier=\"{file.Tier}\" tokens=\"{file.TokenCount}\">");
            var provenance = ProvenanceFormatter.Format(file.Provenance);
            if (provenance.Length > 0)
            {
                builder.AppendLine("<!--");
                builder.AppendLine(provenance);
                builder.AppendLine("-->");
            }

            builder.AppendLine(file.Content);
            builder.AppendLine("</file>");
        }

        return builder.ToString();
    }

    private static string EmitMarkdown(ContextPlan plan, RenderedContext rendered, string? root, string? changedSince, IReadOnlyCollection<string> unchanged, string? apiDeltaSection, string? claimsSection = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Fuse semantic context");
        builder.AppendLine();
        builder.AppendLine("```");
        builder.Append(SemanticManifestBuilder.Build(plan, root, changedSince, apiDeltaSection, claimsSection));
        builder.AppendLine("```");
        builder.AppendLine();

        foreach (var file in rendered.Files)
        {
            if (unchanged.Contains(file.Path))
            {
                builder.AppendLine($"## {file.Path}  [{file.Role}] (unchanged in session, body omitted)");
                builder.AppendLine();
                continue;
            }

            builder.AppendLine($"## {file.Path}  [{file.Role}/{file.Tier}]");
            var summary = ProvenanceFormatter.Summarize(file.Provenance);
            if (summary != "seed")
                builder.AppendLine($"_included via {summary}_");
            builder.AppendLine();
            builder.AppendLine("```csharp");
            builder.AppendLine(file.Content);
            builder.AppendLine("```");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string EmitJson(ContextPlan plan, RenderedContext rendered, string? root, string? changedSince, IReadOnlyCollection<string> unchanged, string? apiDeltaSection, string? claimsSection = null)
    {
        var dto = new ContextJsonDto(
            Mode: plan.Mode,
            Root: root,
            ChangedSince: changedSince,
            Files: rendered.Files.Count,
            EstimatedTokens: rendered.Files.Where(f => !unchanged.Contains(f.Path)).Sum(f => f.TokenCount),
            Entries: rendered.Files
                .Select(f => unchanged.Contains(f.Path)
                    ? new ContextFileDto(f.Path, f.Role, f.Tier.ToString(), f.Score, 0, f.Provenance, string.Empty, Unchanged: true)
                    : new ContextFileDto(f.Path, f.Role, f.Tier.ToString(), f.Score, f.TokenCount, f.Provenance, f.Content))
                .ToList(),
            Notes: plan.Warnings,
            ApiDelta: string.IsNullOrWhiteSpace(apiDeltaSection) ? null : apiDeltaSection.TrimEnd(),
            Claims: string.IsNullOrWhiteSpace(claimsSection) ? null : claimsSection.TrimEnd());

        return JsonSerializer.Serialize(dto, FuseContextJsonContext.Default.ContextJsonDto);
    }

    private static string EscapeAttribute(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
}

/// <summary>The output format for an emitted semantic context.</summary>
public enum ContextOutputFormat
{
    /// <summary>An XML envelope with a manifest comment and file elements.</summary>
    Xml,

    /// <summary>A Markdown document with a manifest block and fenced file sections.</summary>
    Markdown,

    /// <summary>A JSON document.</summary>
    Json,
}
