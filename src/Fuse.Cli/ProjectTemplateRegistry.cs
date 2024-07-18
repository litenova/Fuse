using System.Collections.Immutable;

namespace Fuse.Cli;

public static class ProjectTemplateRegistry
{
    private static readonly ImmutableDictionary<ProjectTemplate, (string[] Extensions, string[] ExcludeFolders)> TemplateDefaults;

    static ProjectTemplateRegistry()
    {
        var builder = ImmutableDictionary.CreateBuilder<ProjectTemplate, (string[] Extensions, string[] ExcludeFolders)>();

        builder[ProjectTemplate.Generic] = (
            [".txt", ".md", ".json", ".xml", ".yaml", ".yml"],
            [".git", ".svn", ".hg", "node_modules", ".vscode", ".idea"]
        );

        builder[ProjectTemplate.DotNet] = (
            [
                ".cs", ".xaml", ".cshtml", ".csproj", ".config", ".json", ".xml", ".resx", ".razor", ".json", ".md", ".txt", ".props", ".targets", ".yml", ".yaml", ".scriban", ".bat", ".sh", ".ps1",
                ".cmd", ".nuspec"
            ],
            ["bin", "obj", ".vs", ".git", ".idea"]
        );

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

        TemplateDefaults = builder.ToImmutable();
    }

    public static (string[] Extensions, string[] ExcludeFolders) GetTemplate(ProjectTemplate template) =>
        TemplateDefaults.TryGetValue(template, out var defaults) ? defaults : TemplateDefaults[ProjectTemplate.Generic];
}