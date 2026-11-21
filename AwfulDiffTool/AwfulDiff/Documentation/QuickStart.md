# Quick Start — AwfulDiff

AwfulDiff is a WPF text/hex file comparison and 3-way merge tool for Windows. It runs as a standalone desktop app and can also be invoked from the command line by version-control tools (Perforce, git, SourceTree).

---

## 1. Requirements

### To run the program

| Requirement | Details |
|---|---|
| OS | Windows 10 or later (64-bit recommended) |
| Runtime | **.NET 9 Desktop Runtime** — *only* if you use a framework-dependent build (the default). A self-contained single-file publish (Section 4.2) needs **nothing** installed. |
| Privileges | None — the app runs as an invoker, no administrator rights required (`app.manifest`: `asInvoker`). |
| Display | Fully DPI-aware (Per-Monitor v2); works correctly on mixed-DPI multi-monitor setups. |

### To build the program

| Requirement | Details |
|---|---|
| OS | Windows 10/11 (WPF projects only build on Windows) |
| SDK | **.NET SDK 9.x** or later (`net9.0-windows` target) |
| Workload | None — the project uses only the built-in WPF + WindowsForms framework references |
| NuGet | **Zero package dependencies** — restore is trivial and works offline once the SDK is installed |
| Tools | `git` (optional, for cloning), Visual Studio 2022 17.10+ or just the `dotnet` CLI |

Verify your SDK:

```powershell
dotnet --list-sdks        # must show a 9.x entry
dotnet --list-runtimes    # look for Microsoft.WindowsDesktop.App 9.x (to *run* framework-dependent builds)
```

---

## 2. Getting the Source

There is no solution file — the repo root contains a single project, `AwfulDiff.csproj`. All commands below are run from the project directory:

---

## 3. Building

### 3.1 Debug build (development)

```powershell
dotnet build --configuration Debug
```

Output lands in:

```
bin\Debug\net9.0-windows\AwfulDiff.exe
```

Includes debug symbols (`.pdb`), no optimizations — ideal for development and debugging.

### 3.2 Release build (production)

```powershell
dotnet build --configuration Release
```

Output lands in:

```
bin\Release\net9.0-windows\AwfulDiff.exe
```

Optimized, no debug symbols. This is a **framework-dependent** build: the target machine needs the .NET 9 Desktop Runtime.

### 3.3 Running during development

```powershell
dotnet run --configuration Debug
dotnet run --configuration Release -- left.txt right.txt   # open straight into a comparison
```

---

## 4. Packaging for Distribution

There are no publish profiles (`.pubxml`) or installer projects in the repo — packaging is done with `dotnet publish` command-line properties. Two supported flavors:

### 4.1 Framework-dependent publish (small, needs .NET 9 Desktop Runtime)

```powershell
dotnet publish --configuration Release
```

Output folder:

```
bin\Release\net9.0-windows\publish\
```

Contains `AwfulDiff.exe` plus a handful of small support files (`.dll`, `.deps.json`, `.runtimeconfig.json`). Copy the whole folder to the target machine; the machine must have the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) installed.

### 4.2 Single-file, self-contained publish (recommended for distribution)

One executable, no runtime installation required on the target machine:

```powershell
dotnet publish --configuration Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output folder:

```
bin\Release\net9.0-windows\win-x64\publish\AwfulDiff.exe
```

A single ~163 MB `AwfulDiff.exe` (it embeds the .NET runtime). Copy that one file anywhere on a Windows 10/11 x64 machine and run it — no installer, no prerequisites.

> **Notes**
> - The publish folder also contains a small `AwfulDiff.pdb` (~190 KB) with debug symbols — safe to delete for distribution.
> - The app has no native dependencies or external assets, so nothing else needs to ship alongside it.
> - Trimming (`PublishTrimmed`) is *not* used and is not recommended for WPF apps.
> - Only `win-x64` is supported in practice (WPF + the manifest target). There is no ARM build configured.

### 4.3 Installing / registering

There is no installer. Typical deployment:

1. Copy the published `AwfulDiff.exe` (or folder) to a stable location, e.g. `C:\Tools\AwfulDiff\`.
2. Optionally add that directory to `PATH` so `AwfulDiff` can be called from any shell.
3. Point your VCS tools at it — see the *User Manual* → "Command-Line & VCS Integration" for Perforce (`P4DIFF`/`P4MERGE`), git (`difftool`/`mergetool`), and SourceTree configuration.

---

## 5. Smoke Test

After building or publishing, verify the tool works:

```powershell
# help text (prints usage and exits 0)
AwfulDiff.exe -h

# 2-way diff of two files
AwfulDiff.exe fileA.txt fileB.txt

# 3-way merge (base-first argument order)
AwfulDiff.exe base.txt mine.txt theirs.txt -o merged.txt
```

Exit codes: `0` in diff mode, or in merge mode when the merged result was written; `1` when a merge is discarded/left unresolved or a file fails to load.
