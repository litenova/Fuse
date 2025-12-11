using System.Collections.Immutable;
using Fuse.Core;

namespace Fuse.Infrastructure;

public static class ProjectTemplateRegistry
{
    private static readonly ImmutableDictionary<ProjectTemplate, (string[] Extensions, string[] ExcludeFolders)> TemplateDefaults;
    private static readonly ImmutableDictionary<ProjectTemplate, string[]> ExcludedPatterns;

    static ProjectTemplateRegistry()
    {
        var builder = ImmutableDictionary.CreateBuilder<ProjectTemplate, (string[] Extensions, string[] ExcludeFolders)>();
        var patternsBuilder = ImmutableDictionary.CreateBuilder<ProjectTemplate, string[]>();

        builder[ProjectTemplate.Generic] = (
            [".txt", ".md", ".json", ".xml", ".yaml", ".yml"],
            [".git", ".svn", ".hg", "node_modules", ".vscode", ".idea"]
        );

        builder[ProjectTemplate.DotNet] = (
            [
                ".cs", ".xaml", ".cshtml", ".csproj", ".config", ".json", ".xml", ".resx", ".razor", ".json", ".md", ".txt", ".props", ".targets", ".yml", ".yaml", ".scriban", ".bat", ".sh", ".ps1",
                ".cmd", ".nuspec", ".scss", ".css", ".html", ".htm", ".sql", ".feature"
            ],
            ["bin", "obj", ".vs", ".git", ".idea"]
        );

        // Add patterns to exclude for DotNet - specifically the generated SpecFlow/ReqnRoll files
        patternsBuilder[ProjectTemplate.DotNet] =
        [
            "*.feature.cs",                // Exclude generated feature files from SpecFlow/ReqnRoll
            "*Steps.g.cs",                 // Exclude generated step files
            "*.AssemblyHooks.cs",          // Exclude assembly hook files from SpecFlow/ReqnRoll
            "*.g.cs",                      // Exclude any generated C# files
            "*.g.i.cs",                    // Exclude designer generated code
            "*.Designer.cs",               // Exclude designer generated code
            "*.designer.cs",               // Exclude designer generated code (lowercase variant)
            "*_i.c",                       // Exclude COM interop files
            "*.generated.cs",              // Exclude other generated code
            "TemporaryGeneratedFile_*.cs", // Exclude temporary generated files
            "*.Cache.cs",                  // Exclude cache files
            "*.cache",                     // Exclude other cache files
            "*.resources",                 // Exclude resource files
            "*.baml",                      // Exclude compiled XAML
            "ServiceReference.cs",         // Exclude service reference code
            "Reference.cs",                // Exclude reference classes
            "AssemblyInfo.cs",             // Exclude assembly info files
            "*.xsd.cs"                     // Exclude XML schema generated code
        ];

        builder[ProjectTemplate.Java] = (
            [".java", ".gradle", ".xml", ".properties", ".jar", ".jsp", ".jspx", ".class"],
            ["build", "target", ".gradle", ".mvn", "node_modules", ".git"]
        );

        builder[ProjectTemplate.Python] = (
            [".py", ".pyc", ".pyd", ".pyo", ".pyw", ".pyx", ".pxd", ".pxi", ".ipynb", ".req", ".txt"],
            ["__pycache__", ".venv", "venv", "env", ".tox", "dist", "build", ".git", ".pytest_cache"]
        );

        builder[ProjectTemplate.JavaScript] = (
            [".js", ".jsx", ".json", ".ts", ".tsx", ".html", ".css", ".scss", ".less", ".mjs"],
            ["node_modules", "dist", "build", "coverage", ".next", ".nuxt", ".git"]
        );

        builder[ProjectTemplate.TypeScript] = (
            [".ts", ".tsx", ".js", ".jsx", ".json", ".html", ".css", ".scss", ".less"],
            ["node_modules", "dist", "build", "coverage", ".next", ".nuxt", ".git"]
        );

        builder[ProjectTemplate.Ruby] = (
            [".rb", ".rake", ".gemspec", "Gemfile", "Rakefile", ".erb", ".haml", ".slim"],
            ["vendor", ".bundle", "coverage", "tmp", "log", ".git"]
        );

        builder[ProjectTemplate.Go] = (
            [".go", ".mod", ".sum"],
            ["vendor", "bin", ".git"]
        );

        builder[ProjectTemplate.Rust] = (
            [".rs", ".toml", ".lock"],
            ["target", ".cargo", ".git"]
        );

        builder[ProjectTemplate.Php] = (
            [".php", ".phtml", ".php7", ".phps", ".php-s", ".pht", ".phar"],
            ["vendor", "node_modules", ".git"]
        );

        builder[ProjectTemplate.CppCSharp] = (
            [".cpp", ".hpp", ".h", ".c", ".cc", ".cs", ".csproj", ".sln"],
            ["bin", "obj", "Debug", "Release", "x64", "x86", ".vs", ".git"]
        );

        builder[ProjectTemplate.Swift] = (
            [".swift", ".xib", ".storyboard", ".xcodeproj", ".pbxproj", ".plist"],
            [".build", "Pods", ".git"]
        );

        builder[ProjectTemplate.Kotlin] = (
            [".kt", ".kts", ".java", ".xml", ".gradle"],
            ["build", ".gradle", ".idea", ".git"]
        );

        builder[ProjectTemplate.Scala] = (
            [".scala", ".sbt", ".sc"],
            ["target", "project/target", ".bloop", ".metals", ".git"]
        );

        builder[ProjectTemplate.Dart] = (
            [".dart", ".yaml", ".lock"],
            ["build", ".dart_tool", ".pub-cache", ".git"]
        );

        builder[ProjectTemplate.Lua] = (
            [".lua", ".rockspec"],
            [".git"]
        );

        builder[ProjectTemplate.Perl] = (
            [".pl", ".pm", ".t"],
            ["blib", "_build", ".git"]
        );

        builder[ProjectTemplate.R] = (
            [".R", ".Rmd", ".Rproj", ".RData", ".rds"],
            [".Rproj.user", ".Rhistory", ".RData", ".Ruserdata", ".git"]
        );

        builder[ProjectTemplate.VbNet] = (
            [".vb", ".vbproj", ".config", ".settings", ".resx", ".sln"],
            ["bin", "obj", ".vs", "packages", "node_modules", ".git"]
        );

        builder[ProjectTemplate.Fsharp] = (
            [".fs", ".fsi", ".fsx", ".fsproj", ".config", ".sln"],
            ["bin", "obj", ".vs", "packages", "node_modules", ".git"]
        );

        builder[ProjectTemplate.Clojure] = (
            [".clj", ".cljs", ".cljc", ".edn"],
            ["target", ".cpcache", ".git"]
        );

        builder[ProjectTemplate.Haskell] = (
            [".hs", ".lhs", ".cabal", ".hs-boot"],
            ["dist", "dist-newstyle", ".stack-work", ".git"]
        );

        builder[ProjectTemplate.Erlang] = (
            [".erl", ".hrl", ".app.src", "rebar.config"],
            ["_build", ".rebar3", ".git"]
        );

        builder[ProjectTemplate.Elixir] = (
            [".ex", ".exs", ".eex", ".leex", "mix.exs"],
            ["_build", "deps", ".git"]
        );

        builder[ProjectTemplate.Infrastructure] = (

            // Extensions to include
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

            // Directories to exclude
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

        // Patterns to exclude for Infrastructure - specifically Terraform and related files
        patternsBuilder[ProjectTemplate.Infrastructure] =
        [
            "*.tfstate",          // Terraform state files
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

        TemplateDefaults = builder.ToImmutable();
        ExcludedPatterns = patternsBuilder.ToImmutable();
    }

    public static (string[] Extensions, string[] ExcludeFolders) GetTemplate(ProjectTemplate template)
    {
        return TemplateDefaults.TryGetValue(template, out var defaults) ? defaults : TemplateDefaults[ProjectTemplate.Generic];
    }

    public static string[] GetExcludedPatterns(ProjectTemplate template)
    {
        return ExcludedPatterns.TryGetValue(template, out var patterns) ? patterns : Array.Empty<string>();
    }
}