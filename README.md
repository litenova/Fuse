# Fuse

**Fuse** is a powerful, developer-centric CLI tool that merges source code files into a single, token-optimized output file. Built for preparing codebases for **Large Language Models (LLMs)**, documentation, and code analysis, Fuse intelligently collects, minifies, and combines your project files so they fit within LLM context windows.

---

## Features

### Smart Project Templates
Pre-configured settings for **25+ languages and frameworks**. Each template knows which file extensions to include and which directories to skip — no manual configuration needed.

### Intelligent Minification
Built-in minifiers for **10 file types** dramatically reduce token count while preserving semantic meaning:
- **C#** — Remove comments, `using` statements, namespace declarations, `#region` directives, attributes, and redundant keywords. Includes an aggressive mode that compresses syntax onto fewer lines.
- **HTML / Razor** — Strip whitespace, collapse attributes, remove comments.
- **CSS / SCSS** — Remove comments, collapse whitespace, strip unnecessary semicolons.
- **JavaScript** — Remove comments and condense whitespace.
- **JSON** — Compact formatting (remove indentation and whitespace).
- **XML / .csproj / .props / .targets** — Minify XML structure.
- **Markdown** — Remove excessive blank lines and trailing whitespace.
- **YAML** — Remove comments and condense whitespace.

### Token Counting & Splitting
- Uses the **cl100k_base** tokenizer (GPT-4 / GPT-3.5-turbo) for accurate token estimation.
- **`--max-tokens`** — Hard stop: halt processing when a global token limit is reached.
- **`--split-tokens`** — Automatically split the output into multiple files when a threshold is exceeded (default: 800,000 tokens), so each file fits within standard LLM context windows.
- Token count is embedded in the output filename (e.g., `MyProject_2026-02-12_1430_554k.txt`).
- **Top Token Consumers** — After fusion, displays the 5 files consuming the most tokens.

### Git Aware
Automatically parses `.gitignore` files up the directory tree and excludes matching files — no extra flags needed.

### Safety & Filtering
- Detects and **skips binary files** automatically.
- **Max file size** filter to skip oversized files.
- Skips trivial content (empty JSON `{}`, empty arrays `[]`, whitespace-only files).
- Excludes generated/noise files via per-template glob patterns (e.g., `*.Designer.cs`, `*.min.js`, `package-lock.json`).

### Clean, LLM-Optimized Output
Output uses simplified XML tags for optimal LLM parsing:
```xml
<file path="src/Program.cs">
// file content here
</file>
```
- Optional **file metadata** (size, modification date) via `--include-metadata`.
- Filenames include token count and timestamp for easy identification.
- Multi-part outputs are numbered (`_part1_800k.txt`, `_part2_554k.txt`).

---

## Installation

Fuse is a cross-platform **.NET Global Tool** that runs on Windows, macOS, and Linux.

### Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/download) or later.

### Local Installation (from source)

**Windows:**
```cmd
install.bat
```

**Manual (any OS):**
```bash
# 1. Pack the tool
dotnet pack src/Fuse.Cli/Fuse.Cli.csproj -c Release

# 2. Install globally from local source
dotnet tool install -g Fuse --add-source src/Fuse.Cli/nupkg
```

Verify the installation:
```bash
fuse --help
```

---

## Usage

```bash
fuse [command] [options]
```

### Commands

| Command       | Description                                                                          |
|---------------|--------------------------------------------------------------------------------------|
| `fuse`        | **Default.** Generic fusion. Specify a template with `--template` or use `--only-extensions`. |
| `fuse dotnet` | Optimized for .NET — includes C#-specific minification and reduction options.        |
| `fuse wiki`   | Optimized for Azure DevOps Wikis — processes Markdown files only.                    |

---

### Global Options (available on all commands)

#### Directory & Output

| Option          | Description                                                     | Default             |
|-----------------|-----------------------------------------------------------------|---------------------|
| `--directory`   | The root directory to scan for files.                           | Current directory   |
| `--output`      | Directory where the output file(s) will be written.             | My Documents folder |
| `--name`        | Custom output filename (without extension).                     | Auto-generated      |
| `--overwrite`   | Overwrite existing output files.                                | `true`              |

#### File Filtering

| Option                    | Description                                                              | Default  |
|---------------------------|--------------------------------------------------------------------------|----------|
| `--only-extensions`       | Process **only** these comma-separated extensions, ignoring all template defaults. | —        |
| `--recursive`             | Search subdirectories recursively.                                       | `true`   |
| `--max-file-size`         | Max file size in KB to process (`0` = unlimited).                        | `0`      |
| `--ignore-binary`         | Skip binary files.                                                       | `true`   |
| `--exclude-test-projects` | Exclude all test project directories (unit, integration, e2e, benchmarks). | `false`  |
| `--respect-git-ignore`    | Honor `.gitignore` rules found in the directory tree.                    | `true`   |

#### Content & Metadata

| Option               | Description                                           | Default  |
|----------------------|-------------------------------------------------------|----------|
| `--include-metadata` | Include file size and modification date in the output. | `false`  |

#### Token Management

| Option            | Description                                                          | Default   |
|-------------------|----------------------------------------------------------------------|-----------|
| `--max-tokens`    | Hard stop — halt processing when global token count is reached.      | —         |
| `--split-tokens`  | Split output into new files when this token count is exceeded.       | `800000`  |
| `--show-token-count` | Display estimated token count in the completion summary.          | `true`    |

---

### `fuse dotnet` Options

All global options above, plus:

| Option                       | Description                                                                  | Default  |
|------------------------------|------------------------------------------------------------------------------|----------|
| `--remove-csharp-namespaces` | Remove `namespace` declarations from C# files.                              | `false`  |
| `--remove-csharp-comments`   | Remove single-line, multi-line, and XML doc comments from C# files.          | `false`  |
| `--remove-csharp-regions`    | Remove `#region` / `#endregion` directives from C# files.                   | `false`  |
| `--remove-csharp-usings`     | Remove `using` directives from C# files.                                    | `false`  |
| `--aggressive`               | Aggressive reduction — remove attributes, redundant keywords, compress auto-properties. | `false`  |
| `--minify-xml-files`         | Minify XML-based files (`.csproj`, `.xml`, `.props`, `.targets`).           | `true`   |
| `--minify-html-and-razor`    | Minify HTML and Razor files (`.html`, `.cshtml`, `.razor`).                 | `true`   |
| `--exclude-unit-test-projects` | Exclude only unit test directories (keeps integration tests & benchmarks). | `false`  |
| `--all`                      | Enable **all** optimizations at once (namespaces, comments, regions, usings, aggressive). | `false`  |

---

## Examples

**1. Fuse a .NET project with all optimizations for maximum token savings:**
```bash
fuse dotnet -d ./src --all
```

**2. Fuse a .NET project, stripping comments and usings:**
```bash
fuse dotnet -d ./src --remove-csharp-comments --remove-csharp-usings
```

**3. Fuse a .NET project, excluding test projects but keeping integration tests:**
```bash
fuse dotnet -d ./src --exclude-unit-test-projects
```

**4. Fuse a Python project using a template:**
```bash
fuse -d ./my-app --template Python --exclude-test-projects
```

**5. Fuse only specific file types (override all template defaults):**
```bash
fuse -d ./frontend --only-extensions .ts,.tsx,.css
```

**6. Fuse an Azure DevOps wiki repository:**
```bash
fuse wiki -d ./wiki-repo --include-metadata
```

**7. Fuse with a hard token limit of 100k:**
```bash
fuse dotnet -d ./src --max-tokens 100000
```

**8. Fuse a large project with automatic file splitting:**
```bash
fuse dotnet -d ./src --split-tokens 500000
```

**9. Fuse to a specific output location with a custom name:**
```bash
fuse dotnet -d ./src -o ./output --name my-context
```

---

## Supported Templates

Each template defines sensible file extensions and excluded directories for its ecosystem.

| Category           | Templates                                                        |
|--------------------|------------------------------------------------------------------|
| **Web**            | JavaScript, TypeScript, PHP                                      |
| **Backend**        | .NET (C#), Java, Python, Go, Rust, Ruby                         |
| **Mobile**         | Swift, Kotlin, Dart (Flutter)                                    |
| **Functional**     | F#, Haskell, Clojure, Elixir, Erlang, Scala                     |
| **Systems**        | C++/C# (mixed)                                                   |
| **Scripting**      | Lua, Perl, R, VB.NET                                             |
| **Infrastructure** | Terraform, Kubernetes, Ansible, Helm (`.tf`, `.yaml`, `.hcl`, etc.) |
| **Documentation**  | Azure DevOps Wiki (Markdown only), Generic (text/json/yaml/xml)  |

### Template Highlights

- **DotNet** — Includes `.cs`, `.razor`, `.cshtml`, `.csproj`, `.json`, `.yml`, `.scss`, `.sql`, and more. Excludes `bin`, `obj`, `.vs`, `packages`, `artifacts`, `TestResults`. Filters out generated files (`*.Designer.cs`, `*.g.cs`), lock files, minified assets, and resource files.
- **Infrastructure** — Includes `.tf`, `.tfvars`, `.yaml`, `.yml`, `.json`, `.sh`, `.ps1`, `.hcl`. Excludes Terraform state files, plan files, lock files, and crash logs.
- **AzureDevOpsWiki** — Includes only `.md` files, excludes `.git` and `.attachments`.

---

## Output Format

Fuse generates plain `.txt` files with each source file wrapped in XML tags:

```xml
<file path="src/Models/User.cs">
public class User
{
    public int Id { get; set; }
    public string Name { get; set; }
}
</file>

<file path="src/Services/UserService.cs">
public class UserService
{
    public User GetById(int id) => _repo.Find(id);
}
</file>
```

### Filename Convention

Output filenames encode useful metadata:

| Scenario        | Example Filename                                |
|-----------------|-------------------------------------------------|
| Default         | `MyProject_2026-02-12_1430_554k.txt`            |
| With `--all`    | `MyProject_all_2026-02-12_1430_320k.txt`        |
| Multi-part      | `MyProject_part1_800k.txt`, `MyProject_part2_554k.txt` |
| Custom name     | `my-context_50k.txt`                            |

---

## Architecture

Fuse follows a clean, layered architecture:

```
Fuse.Cli          → Presentation — CLI commands, argument parsing (DotMake.CommandLine)
Fuse.Engine       → Application  — Orchestration, file collection, output building
Fuse.Core         → Domain       — Entities, options, abstractions, templates
Fuse.Minifiers    → Infrastructure — File-type-specific minification
```

Key components:
- **FuseEngine** — Central orchestrator: resolves config → collects files → builds output.
- **ProjectTemplateRegistry** — Static registry of all 25+ template configurations.
- **FileCollector** — Enumerates files with filtering (extensions, directories, gitignore, binary detection, test projects, glob patterns).
- **ContentProcessor** — Reads files, applies trimming/condensation, dispatches to the appropriate minifier.
- **OutputBuilder** — Writes the fused output with token tracking (TikToken), file splitting, and filename generation.
- **GitIgnoreParser** — Walks the directory tree collecting `.gitignore` patterns.

---

## Contributing

1. Clone the repository.
2. Open `Fuse.sln` in your IDE.
3. Make changes and run `install.bat` to build, pack, and install locally.
4. Test with `fuse --help` or `fuse dotnet -d ./src`.

---

## License

Licensed under the MIT License.
