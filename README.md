# Fuse

Fuse is a flexible file combining tool for developers, designed to streamline the process of merging multiple files from
a project directory into a single output file. It offers a wide range of customization options to suit various project
types and developer preferences.

## Table of Contents

1. [Installation](#installation)
2. [Usage](#usage)
3. [Command-Line Options](#command-line-options)
4. [Project Templates](#project-templates)
5. [Features](#features)
6. [Examples](#examples)
7. [Contributing](#contributing)
8. [License](#license)

## Installation

### Windows

1. Download the `install_fuse_win64.bat` script.
2. Run the script with administrator privileges.
3. The script will install Fuse to `C:\Program Files\FuseTool` and add it to your system PATH.
4. Restart your command prompt to use the `fuse` command.

### Other Platforms

For other platforms, you can build the project from source:

1. Ensure you have .NET SDK 8.0 or later installed.
2. Clone the repository: `git clone https://github.com/your-repo/Fuse.git`
3. Navigate to the project directory: `cd Fuse/src/Fuse.Cli`
4. Build the project: `dotnet build -c Release`
5. Run the tool: `dotnet run -- [options]`

## Usage

```
fuse [options]
```

## Command-Line Options

- `--directory, -d <path>`: Path to the directory to process (required).
- `--output, -o <path>`: Path to the output directory where the combined file will be saved (required).
- `--template, -t <template>`: Project template to use (optional).
- `--extensions, -e <list>`: Comma-separated list of file extensions to include in the processing (optional).
- `--exclude, -x <list>`: Comma-separated list of directories to exclude from processing (optional).
- `--name, -n <filename>`: Name of the output file without extension (optional).
- `--overwrite, -w`: Whether to overwrite the output file if it already exists (default: true).
- `--recursive, -r`: Whether to search recursively through subdirectories (default: true).
- `--trim`: Whether to trim leading and trailing whitespace from each line in the file contents (default: true).
- `--max-file-size <size>`: Maximum file size in KB to process. Files larger than this will be skipped. Set to 0 for
  unlimited size (default: 10240).
- `--ignore-binary`: Whether to ignore binary files (default: true).
- `--aggressive-minify`: Whether to aggressively minify .cs and .razor files, removing most whitespace including
  newlines (default: false).
- `--include-metadata`: Whether to include file metadata in the output file (default: false).
- `--condense`: Whether to apply line condensing to the output file (default: true).

## Project Templates

Fuse supports various project templates to automatically set appropriate file extensions and exclusions:

- Generic
- DotNet
- Java
- Python
- JavaScript
- TypeScript
- Ruby
- Go
- Rust
- Php
- CppCSharp
- Swift
- Kotlin
- Scala
- Dart
- Lua
- Perl
- R
- VbNet
- Fsharp
- Clojure
- Haskell
- Erlang
- Elixir

Use the `--template` option to specify a template.

## Examples

1. Basic usage:
   ```
   fuse --directory C:\MyProject --output C:\Output
   ```

2. Using a project template:
   ```
   fuse -d C:\MyProject -o C:\Output -t DotNet
   ```

3. Custom file extensions:
   ```
   fuse -d C:\MyProject -o C:\Output -e .cs,.js,.html
   ```

4. Exclude specific directories:
   ```
   fuse -d C:\MyProject -o C:\Output -x bin,obj,node_modules
   ```

5. Aggressive minification:
   ```
   fuse -d C:\MyProject -o C:\Output --aggressive-minify
   ```

6. Include metadata:
   ```
   fuse -d C:\MyProject -o C:\Output --include-metadata
   ```
