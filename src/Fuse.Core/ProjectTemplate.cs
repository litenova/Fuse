// -----------------------------------------------------------------------
// <copyright file="ProjectTemplate.cs" company="Fuse">
//     Copyright (c) Fuse. All rights reserved.
//     Licensed under the MIT License. See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Fuse.Core;

/// <summary>
///     Defines the available project templates for file fusion operations.
/// </summary>
/// <remarks>
///     <para>
///         Each template provides sensible defaults for file extensions to include
///         and directories to exclude based on the project type. This helps users
///         quickly fuse common project structures without manual configuration.
///     </para>
///     <para>
///         Templates can be selected via the CLI using subcommands (e.g., <c>fuse dotnet</c>)
///         or programmatically via <see cref="FuseOptions.Template" />.
///     </para>
/// </remarks>
public enum ProjectTemplate
{
    /// <summary>
    ///     A generic template for text-based files with minimal assumptions.
    /// </summary>
    /// <remarks>
    ///     Includes: .txt, .md, .json, .xml, .yaml, .yml
    ///     Excludes: .git, .svn, .hg, node_modules, .vscode, .idea
    /// </remarks>
    Generic,

    /// <summary>
    ///     Template optimized for .NET projects (C#, F#, VB.NET, ASP.NET).
    /// </summary>
    /// <remarks>
    ///     Includes: .cs, .xaml, .cshtml, .csproj, .razor, .json, .md, etc.
    ///     Excludes: bin, obj, .vs, .git, .idea
    /// </remarks>
    DotNet,

    /// <summary>
    ///     Template for Java projects (Maven, Gradle).
    /// </summary>
    Java,

    /// <summary>
    ///     Template for Python projects.
    /// </summary>
    Python,

    /// <summary>
    ///     Template for JavaScript projects.
    /// </summary>
    JavaScript,

    /// <summary>
    ///     Template for TypeScript projects.
    /// </summary>
    TypeScript,

    /// <summary>
    ///     Template for Ruby projects (Rails, gems).
    /// </summary>
    Ruby,

    /// <summary>
    ///     Template for Go projects.
    /// </summary>
    Go,

    /// <summary>
    ///     Template for Rust projects (Cargo).
    /// </summary>
    Rust,

    /// <summary>
    ///     Template for PHP projects.
    /// </summary>
    Php,

    /// <summary>
    ///     Template for mixed C++ and C# projects.
    /// </summary>
    CppCSharp,

    /// <summary>
    ///     Template for Swift projects (iOS, macOS).
    /// </summary>
    Swift,

    /// <summary>
    ///     Template for Kotlin projects (Android, JVM).
    /// </summary>
    Kotlin,

    /// <summary>
    ///     Template for Scala projects.
    /// </summary>
    Scala,

    /// <summary>
    ///     Template for Dart projects (Flutter).
    /// </summary>
    Dart,

    /// <summary>
    ///     Template for Lua projects.
    /// </summary>
    Lua,

    /// <summary>
    ///     Template for Perl projects.
    /// </summary>
    Perl,

    /// <summary>
    ///     Template for R projects (statistics, data science).
    /// </summary>
    R,

    /// <summary>
    ///     Template for Visual Basic .NET projects.
    /// </summary>
    VbNet,

    /// <summary>
    ///     Template for F# projects.
    /// </summary>
    Fsharp,

    /// <summary>
    ///     Template for Clojure projects.
    /// </summary>
    Clojure,

    /// <summary>
    ///     Template for Haskell projects.
    /// </summary>
    Haskell,

    /// <summary>
    ///     Template for Erlang projects.
    /// </summary>
    Erlang,

    /// <summary>
    ///     Template for Elixir projects (Phoenix).
    /// </summary>
    Elixir,

    /// <summary>
    ///     Template for infrastructure-as-code projects (Terraform, Kubernetes).
    /// </summary>
    /// <remarks>
    ///     Includes: .tf, .tfvars, .yaml, .yml, .json, .md, .sh, .ps1, .hcl
    ///     Excludes: .terraform, node_modules, .git, bin, obj, dist, build
    /// </remarks>
    Infrastructure,

    /// <summary>
    ///     Template for Azure DevOps wiki repositories.
    /// </summary>
    /// <remarks>
    ///     Specifically targets Markdown files for wiki documentation.
    ///     Includes: .md
    ///     Excludes: .git, .attachments
    /// </remarks>
    AzureDevOpsWiki
}