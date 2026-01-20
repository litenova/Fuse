// -----------------------------------------------------------------------
// <copyright file="ProjectTemplateRegistry.cs" company="Fuse">
// Copyright (c) Fuse. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.Immutable;
using Fuse.Core;

namespace Fuse.Engine;

/// <summary>
/// Provides a registry of project template configurations for different project types.
/// </summary>
/// <remarks>
/// <para>
/// This static class maintains immutable dictionaries of template defaults including:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>File extensions to include for each template type</description>
/// </item>
/// <item>
/// <description>Directories to exclude from scanning</description>
/// </item>
/// <item>
/// <description>File patterns to exclude (e.g., generated files)</description>
/// </item>
/// </list>
/// <para>
/// Templates are initialized statically and are thread-safe for concurrent access.
/// </para>
/// </remarks>
public static class ProjectTemplateRegistry
{
    /// <summary>
    /// Stores the default extensions and excluded folders for each template.
    /// </summary>
    private static readonly ImmutableDictionary<ProjectTemplate, (string[] Extensions, string[] ExcludeFolders)> TemplateDefaults;

    /// <summary>
    /// Stores the excluded file patterns for each template.
    /// </summary>
    private static readonly ImmutableDictionary<ProjectTemplate, string[]> ExcludedPatterns;

    /// <summary>
    /// Initializes the static template registry with all predefined templates.
    /// </summary>
    static ProjectTemplateRegistry()
    {
        // Builder for template defaults (extensions and excluded folders)
        var builder = ImmutableDictionary.CreateBuilder<ProjectTemplate, (string[] Extensions, string[] ExcludeFolders)>();

        // Builder for excluded file patterns
        var patternsBuilder = ImmutableDictionary.CreateBuilder<ProjectTemplate, string[]>();

        // ===== Generic Template =====
        // A minimal template for general text files
        builder[ProjectTemplate.Generic] = (
            [".txt", ".md", ".json", ".xml", ".yaml", ".yml"],
            [".git", ".svn", ".hg", "node_modules", ".vscode", ".idea"]
        );

        // ===== .NET Template =====
        // Comprehensive template for C#, F#, VB.NET, and ASP.NET projects
        builder[ProjectTemplate.DotNet] = (
            [
                ".cs", ".xaml", ".cshtml", ".csproj", ".config", ".json", ".xml",
                ".razor", ".md", ".txt", ".props", ".targets", ".yml", ".yaml", ".scriban",
                ".bat", ".sh", ".ps1", ".cmd", ".nuspec", ".scss", ".css", ".html", ".htm",
                ".sql", ".feature", ".editorconfig"
            ],
            [
                "bin", "obj", ".vs", ".git", ".idea",
                "node_modules", // Common in ASP.NET Core SPA templates
                "TestResults",  // dotnet test output
                "packages",     // NuGet packages (legacy)
                "artifacts"     // Common build output folder
            ]
        );

        // Patterns to exclude for .NET - specifically generated files and noise
        patternsBuilder[ProjectTemplate.DotNet] =
        [
            // Generated Code
            "*.feature.cs",                // SpecFlow/ReqnRoll generated feature files
            "*Steps.g.cs",                 // Generated step files
            "*.AssemblyHooks.cs",          // Assembly hook files
            "*.g.cs",                      // Any generated C# files
            "*.g.i.cs",                    // Designer generated code
            "*.Designer.cs",               // Designer generated code
            "*.designer.cs",               // Designer generated code (lowercase variant)
            "*_i.c",                       // COM interop files
            "*.generated.cs",              // Other generated code
            "TemporaryGeneratedFile_*.cs", // Temporary generated files
            "*.Cache.cs",                  // Cache files
            "*.cache",                     // Other cache files
            "*.baml",                      // Compiled XAML
            "ServiceReference.cs",         // Service reference code
            "Reference.cs",                // Reference classes
            "AssemblyInfo.cs",             // Assembly info files
            "*.xsd.cs",                    // XML schema generated code

            // High-Noise / Low-Value Files
            "*.resx",              // Resource files (verbose XML, often binary data)
            "*.resources",         // Compiled resources
            "launchSettings.json", // Local dev config (ports, env vars) - noise for logic
            "packages.lock.json",  // NuGet lock file - noise
            "bundleconfig.json",   // Bundling config - usually low value

            // Web Artifacts (Noise in a source context)
            "*.min.js",          // Minified JavaScript
            "*.min.css",         // Minified CSS
            "*.map",             // Source maps
            "package-lock.json", // NPM lock file
            "yarn.lock"          // Yarn lock file
        ];

        // ===== Java Template =====
        builder[ProjectTemplate.Java] = (
            [".java", ".gradle", ".xml", ".properties", ".jar", ".jsp", ".jspx", ".class"],
            ["build", "target", ".gradle", ".mvn", "node_modules", ".git"]
        );

        // ===== Python Template =====
        builder[ProjectTemplate.Python] = (
            [".py", ".pyc", ".pyd", ".pyo", ".pyw", ".pyx", ".pxd", ".pxi", ".ipynb", ".req", ".txt"],
            ["__pycache__", ".venv", "venv", "env", ".tox", "dist", "build", ".git", ".pytest_cache"]
        );

        // ===== JavaScript Template =====
        builder[ProjectTemplate.JavaScript] = (
            [".js", ".jsx", ".json", ".ts", ".tsx", ".html", ".css", ".scss", ".less", ".mjs"],
            ["node_modules", "dist", "build", "coverage", ".next", ".nuxt", ".git"]
        );

        // ===== TypeScript Template =====
        builder[ProjectTemplate.TypeScript] = (
            [".ts", ".tsx", ".js", ".jsx", ".json", ".html", ".css", ".scss", ".less"],
            ["node_modules", "dist", "build", "coverage", ".next", ".nuxt", ".git"]
        );

        // ===== Ruby Template =====
        builder[ProjectTemplate.Ruby] = (
            [".rb", ".rake", ".gemspec", "Gemfile", "Rakefile", ".erb", ".haml", ".slim"],
            ["vendor", ".bundle", "coverage", "tmp", "log", ".git"]
        );

        // ===== Go Template =====
        builder[ProjectTemplate.Go] = (
            [".go", ".mod", ".sum"],
            ["vendor", "bin", ".git"]
        );

        // ===== Rust Template =====
        builder[ProjectTemplate.Rust] = (
            [".rs", ".toml", ".lock"],
            ["target", ".cargo", ".git"]
        );

        // ===== PHP Template =====
        builder[ProjectTemplate.Php] = (
            [".php", ".phtml", ".php7", ".phps", ".php-s", ".pht", ".phar"],
            ["vendor", "node_modules", ".git"]
        );

        // ===== C++/C# Mixed Template =====
        builder[ProjectTemplate.CppCSharp] = (
            [".cpp", ".hpp", ".h", ".c", ".cc", ".cs", ".csproj", ".sln"],
            ["bin", "obj", "Debug", "Release", "x64", "x86", ".vs", ".git"]
        );

        // ===== Swift Template =====
        builder[ProjectTemplate.Swift] = (
            [".swift", ".xib", ".storyboard", ".xcodeproj", ".pbxproj", ".plist"],
            [".build", "Pods", ".git"]
        );

        // ===== Kotlin Template =====
        builder[ProjectTemplate.Kotlin] = (
            [".kt", ".kts", ".java", ".xml", ".gradle"],
            ["build", ".gradle", ".idea", ".git"]
        );

        // ===== Scala Template =====
        builder[ProjectTemplate.Scala] = (
            [".scala", ".sbt", ".sc"],
            ["target", "project/target", ".bloop", ".metals", ".git"]
        );

        // ===== Dart Template =====
        builder[ProjectTemplate.Dart] = (
            [".dart", ".yaml", ".lock"],
            ["build", ".dart_tool", ".pub-cache", ".git"]
        );

        // ===== Lua Template =====
        builder[ProjectTemplate.Lua] = (
            [".lua", ".rockspec"],
            [".git"]
        );

        // ===== Perl Template =====
        builder[ProjectTemplate.Perl] = (
            [".pl", ".pm", ".t"],
            ["blib", "_build", ".git"]
        );

        // ===== R Template =====
        builder[ProjectTemplate.R] = (
            [".R", ".Rmd", ".Rproj", ".RData", ".rds"],
            [".Rproj.user", ".Rhistory", ".RData", ".Ruserdata", ".git"]
        );

        // ===== VB.NET Template =====
        builder[ProjectTemplate.VbNet] = (
            [".vb", ".vbproj", ".config", ".settings", ".resx", ".sln"],
            ["bin", "obj", ".vs", "packages", "node_modules", ".git"]
        );

        // ===== F# Template =====
        builder[ProjectTemplate.Fsharp] = (
            [".fs", ".fsi", ".fsx", ".fsproj", ".config", ".sln"],
            ["bin", "obj", ".vs", "packages", "node_modules", ".git"]
        );

        // ===== Clojure Template =====
        builder[ProjectTemplate.Clojure] = (
            [".clj", ".cljs", ".cljc", ".edn"],
            ["target", ".cpcache", ".git"]
        );

        // ===== Haskell Template =====
        builder[ProjectTemplate.Haskell] = (
            [".hs", ".lhs", ".cabal", ".hs-boot"],
            ["dist", "dist-newstyle", ".stack-work", ".git"]
        );

        // ===== Erlang Template =====
        builder[ProjectTemplate.Erlang] = (
            [".erl", ".hrl", ".app.src", "rebar.config"],
            ["_build", ".rebar3", ".git"]
        );

        // ===== Elixir Template =====
        builder[ProjectTemplate.Elixir] = (
            [".ex", ".exs", ".eex", ".leex", "mix.exs"],
            ["_build", "deps", ".git"]
        );

        // ===== Infrastructure (IaC) Template =====
        // For Terraform, Kubernetes, Ansible, etc.
        builder[ProjectTemplate.Infrastructure] = (
            [
                ".tf",         // Terraform files
                ".tfvars",     // Terraform variables
                ".yaml",       // Kubernetes/Helm manifests
                ".yml",        // Kubernetes/Helm manifests
                ".json",       // Configuration files
                ".md",         // Documentation
                ".sh",         // Shell scripts
                ".ps1",        // PowerShell scripts
                ".hcl",        // HashiCorp configuration files
                ".tpl",        // Template files
                ".env",        // Environment files
                ".properties", // Properties files
                ".conf",       // Configuration files
                ".config"      // Configuration files
            ],
            [
                ".terraform",    // Terraform working directory
                "node_modules",  // NPM packages
                ".git",          // Git repository
                ".vs",           // Visual Studio files
                ".idea",         // IntelliJ files
                "bin",           // Binary files
                "obj",           // Object files
                "dist",          // Distribution files
                "build",         // Build artifacts
                ".pytest_cache", // Python cache
                "__pycache__",   // Python cache
                "tmp",           // Temporary files
                "temp",          // Temporary files
                "logs"           // Log files
            ]
        );

        // Patterns to exclude for Infrastructure template
        patternsBuilder[ProjectTemplate.Infrastructure] =
        [
            "*.tfstate",          // Terraform state files (sensitive)
            "*.tfstate.backup",   // Terraform state backups
            "*.tfplan",           // Terraform plan files
            "*.tfvars.json",      // Terraform variable files in JSON
            "override.tf",        // Terraform override files
            "override.tf.json",   // Terraform override files in JSON
            "*_override.tf",      // Terraform override files
            "*_override.tf.json", // Terraform override files in JSON
            ".terraformrc",       // Terraform CLI config
            "terraform.rc",       // Terraform CLI config
            "crash.log",          // Terraform crash logs
            "crash.*.log",        // Terraform crash logs
            ".terraform.lock.hcl" // Terraform dependency lock file
        ];

        // ===== Azure DevOps Wiki Template =====
        // Specifically for Azure DevOps wiki repositories
        builder[ProjectTemplate.AzureDevOpsWiki] = (
            [".md"],                 // Wiki consists primarily of Markdown files
            [".git", ".attachments"] // Exclude git and attachments folder
        );

        // Build the immutable dictionaries
        TemplateDefaults = builder.ToImmutable();
        ExcludedPatterns = patternsBuilder.ToImmutable();
    }

    /// <summary>
    /// Gets the template configuration for the specified project template.
    /// </summary>
    /// <param name="template">The project template to retrieve configuration for.</param>
    /// <returns>
    /// A tuple containing the file extensions to include and directories to exclude.
    /// Returns the Generic template configuration if the specified template is not found.
    /// </returns>
    /// <example>
    /// <code>
    /// var (extensions, excludeFolders) = ProjectTemplateRegistry.GetTemplate(ProjectTemplate.DotNet);
    /// // extensions: [".cs", ".csproj", ".json", ...]
    /// // excludeFolders: ["bin", "obj", ".vs", ".git", ".idea"]
    /// </code>
    /// </example>
    public static (string[] Extensions, string[] ExcludeFolders) GetTemplate(ProjectTemplate template)
    {
        return TemplateDefaults.TryGetValue(template, out var defaults)
            ? defaults
            : TemplateDefaults[ProjectTemplate.Generic];
    }

    /// <summary>
    /// Gets the excluded file patterns for the specified project template.
    /// </summary>
    /// <param name="template">The project template to retrieve patterns for.</param>
    /// <returns>
    /// An array of glob patterns that should be excluded from processing.
    /// Returns an empty array if the template has no specific exclusion patterns.
    /// </returns>
    /// <example>
    /// <code>
    /// var patterns = ProjectTemplateRegistry.GetExcludedPatterns(ProjectTemplate.DotNet);
    /// // patterns: ["*.g.cs", "*.Designer.cs", ...]
    /// </code>
    /// </example>
    public static string[] GetExcludedPatterns(ProjectTemplate template)
    {
        return ExcludedPatterns.TryGetValue(template, out var patterns)
            ? patterns
            : [];
    }
}