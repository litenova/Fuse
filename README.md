# Fuse

**Fuse** is a powerful, developer-centric file combining tool designed to merge source code files into a single output
file. It is optimized for preparing codebases for Large Language Models (LLMs), documentation, or code analysis.

## Features

- **Smart Templates**: Pre-configured settings for .NET, Python, Java, JavaScript, and 20+ other languages.
- **Intelligent Minification**: Reduces token count by removing comments and whitespace for C#, HTML, CSS, JSON, XML,
  and more.
- **Git Aware**: Automatically respects `.gitignore` rules to exclude unwanted files.
- **Token Counting**: Estimates GPT-4 tokens to help you stay within context limits.
- **Safety First**: Detects and skips binary files automatically.
- **Clean Output**: Generates a structured text file with clear file markers and optional metadata.

## Installation

Fuse is built as a .NET Global Tool. You can install it easily on Windows, macOS, or Linux.

### Prerequisites

- [.NET SDK 8.0](https://dotnet.microsoft.com/download) or later.

### Local Installation (Development)

To build and install the tool from the source code:

**Windows:**
Run the included script:

```cmd
install.bat
```

**macOS / Linux:**

```bash
chmod +x install.sh
./install.sh
```

### Manual Installation

If you prefer to run the commands manually:

```bash
# 1. Pack the tool
dotnet pack src/Fuse.Cli/Fuse.Cli.csproj -c Release

# 2. Install globally from local source
dotnet tool install -g Fuse --add-source src/Fuse.Cli/nupkg
```

## Usage

Once installed, use the `fuse` command from any terminal.

### Basic Syntax

```bash
fuse [command] [options]
```

### Commands

| Command       | Description                                                                             |
|---------------|-----------------------------------------------------------------------------------------|
| `fuse`        | **Default.** Generic fusion. Uses standard text extensions if no template is specified. |
| `fuse dotnet` | Optimized for .NET (C#, F#, Razor). Adds options to remove namespaces, usings, etc.     |
| `fuse wiki`   | Optimized for Azure DevOps Wikis. Processes Markdown files only.                        |

### Common Options

| Option                    | Alias | Description                                                                  |
|---------------------------|-------|------------------------------------------------------------------------------|
| `--directory`             | `-d`  | The root directory to process (default: current dir).                        |
| `--output`                | `-o`  | Output directory (default: MyDocuments).                                     |
| `--name`                  | `-n`  | Custom output filename.                                                      |
| `--template`              | `-t`  | **(Root command only)** Specify a project template (e.g., `Python`, `Java`). |
| `--only-extensions`       |       | **Override.** Process *only* these extensions, ignoring all defaults.        |
| `--max-tokens`            |       | Stop processing if the estimated token count exceeds this limit.             |
| `--include-metadata`      |       | Add file size and modification date to the output.                           |
| `--exclude-test-projects` |       | Automatically skip folders like `Tests`, `UnitTests`, etc.                   |

## Examples

**1. Fuse a .NET project for an LLM prompt:**

```bash
fuse dotnet -d ./src -o ./output --remove-csharp-comments --remove-csharp-usings
```

**2. Fuse a Python project, excluding tests:**

```bash
fuse -d ./my-app -t Python --exclude-test-projects
```

**3. Fuse only specific files (Override):**

```bash
fuse -d ./frontend --only-extensions .ts,.tsx,.css
```

**4. Create a documentation backup:**

```bash
fuse wiki -d ./docs --include-metadata
```

## Supported Templates

Fuse includes built-in configurations for:

- **Web**: JavaScript, TypeScript, HTML, CSS, PHP
- **Backend**: .NET, Java, Python, Go, Rust, Ruby, Node.js
- **Mobile**: Swift, Kotlin, Dart (Flutter)
- **Functional**: F#, Haskell, Clojure, Elixir, Erlang, Scala
- **Data/Scripting**: R, Lua, Perl, PowerShell, Shell
- **Infrastructure**: Terraform, Kubernetes, Ansible

## Contributing

1. Clone the repository.
2. Open `src/Fuse.sln` in your IDE.
3. Make changes and run `install.bat` to test locally.

## License

Licensed under the MIT License.
