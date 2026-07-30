using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Fuse.Architecture.Tests;

public sealed class ArchitecturePolicyTests
{
    private const int MaximumLines = 500;
    private const int MaximumMethods = 25;
    private const int MaximumConstructorDependencies = 7;

    [Fact]
    public void Production_projects_do_not_reference_test_projects()
    {
        var violations = Directory
            .GetFiles(Path.Combine(RepositoryRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(project => XDocument.Load(project)
                .Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(reference => (Project: Relative(project), Reference: reference.Attribute("Include")?.Value)))
            .Where(item => item.Reference is not null && IsTestPath(item.Reference))
            .Select(item => $"{item.Project} references {item.Reference}")
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Production projects must not reference tests:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Production_code_has_no_mutable_public_static_service_state()
    {
        var violations = new List<string>();
        foreach (var source in ProductionSources())
        {
            foreach (var field in source.Root.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                if (IsPublicStatic(field.Modifiers)
                    && !field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)
                    && !field.Modifiers.Any(SyntaxKind.ConstKeyword))
                {
                    violations.Add($"{source.RelativePath}:{Line(field)} mutable public static field");
                }
            }

            foreach (var property in source.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                var hasSetter = property.AccessorList?.Accessors.Any(accessor =>
                    accessor.IsKind(SyntaxKind.SetAccessorDeclaration)
                    || accessor.IsKind(SyntaxKind.InitAccessorDeclaration)) == true;
                if (IsPublicStatic(property.Modifiers) && hasSetter)
                    violations.Add($"{source.RelativePath}:{Line(property)} settable public static property");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Mutable public static service state is prohibited:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Service_command_rpc_and_mcp_handler_types_stay_bounded()
    {
        var violations = new List<string>();
        foreach (var source in ProductionSources())
        {
            foreach (var type in source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(IsGovernedType))
            {
                var nonEmptyLines = CountNonEmptyLines(source.Text, type.Span);
                var methods = type.Members.OfType<MethodDeclarationSyntax>().Count();
                var maximumDependencies = type.Members
                    .OfType<ConstructorDeclarationSyntax>()
                    .Select(constructor => constructor.ParameterList.Parameters.Count)
                    .DefaultIfEmpty(0)
                    .Max();
                var name = type.Identifier.ValueText;

                if (nonEmptyLines > MaximumLines)
                    violations.Add($"{source.RelativePath}:{Line(type)} {name} has {nonEmptyLines} non-empty lines (limit {MaximumLines})");
                if (methods > MaximumMethods)
                    violations.Add($"{source.RelativePath}:{Line(type)} {name} has {methods} methods (limit {MaximumMethods})");
                if (maximumDependencies > MaximumConstructorDependencies)
                {
                    violations.Add(
                        $"{source.RelativePath}:{Line(type)} {name} has {maximumDependencies} constructor dependencies (limit {MaximumConstructorDependencies})");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Bounded production types violated the architecture policy:" + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<SourceFile> ProductionSources()
    {
        foreach (var path in Directory.GetFiles(Path.Combine(RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (IsGenerated(path))
                continue;

            var text = File.ReadAllText(path);
            yield return new SourceFile(
                Relative(path),
                SourceText.From(text),
                CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot());
        }
    }

    private static bool IsGovernedType(ClassDeclarationSyntax type)
    {
        var name = type.Identifier.ValueText;
        return name.EndsWith("Service", StringComparison.Ordinal)
            || name.EndsWith("Command", StringComparison.Ordinal)
            || name.EndsWith("Rpc", StringComparison.Ordinal)
            || type.AttributeLists
                .SelectMany(list => list.Attributes)
                .Any(attribute => attribute.Name.ToString().EndsWith("McpServerToolType", StringComparison.Ordinal)
                    || attribute.Name.ToString().EndsWith("McpServerToolTypeAttribute", StringComparison.Ordinal));
    }

    private static bool IsPublicStatic(SyntaxTokenList modifiers) =>
        modifiers.Any(SyntaxKind.PublicKeyword) && modifiers.Any(SyntaxKind.StaticKeyword);

    private static int CountNonEmptyLines(SourceText text, TextSpan span)
    {
        var start = text.Lines.GetLineFromPosition(span.Start).LineNumber;
        var end = text.Lines.GetLineFromPosition(span.End).LineNumber;
        return Enumerable.Range(start, end - start + 1)
            .Count(line => !string.IsNullOrWhiteSpace(text.Lines[line].ToString()));
    }

    private static bool IsGenerated(string path) =>
        path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static bool IsTestPath(string path) =>
        path.Replace('\\', '/').Contains("/tests/", StringComparison.OrdinalIgnoreCase)
        || path.Replace('\\', '/').StartsWith("tests/", StringComparison.OrdinalIgnoreCase);

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string Relative(string path) =>
        Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/');

    private static string RepositoryRoot => _repositoryRoot ??= FindRepositoryRoot();

    private static string? _repositoryRoot;

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var current = new DirectoryInfo(Path.GetFullPath(start)); current is not null; current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, "Fuse.slnx")))
                    return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Fuse repository root from the test process.");
    }

    private sealed record SourceFile(string RelativePath, SourceText Text, CompilationUnitSyntax Root);
}
