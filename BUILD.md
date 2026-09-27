# Skua Build Guide

This document provides instructions for building the Skua project from source, including automated build scripts for x64, x86, and WiX installer creation.

## Table of Contents
- [Prerequisites](#prerequisites)
- [Quick Start](#quick-start)
- [Build Scripts](#build-scripts)
- [Manual Building](#manual-building)
- [CI/CD](#cicd)
- [Troubleshooting](#troubleshooting)

## Prerequisites

### Required Software

1. **.NET 10.0 SDK or later**
   - Download from: [Microsoft](https://dotnet.microsoft.com/download)
   - Verify installation: `dotnet --version`
   - Project targets: `net10.0-windows` for applications and UI libraries; `net10.0` for Skua.Core, Skua.Core.Interfaces, Skua.Core.Models and Skua.Core.Utils, and for the macOS projects

2. **Visual Studio 2026** (for MSBuild and WiX support)
   - Workloads required:
     - .NET desktop development
     - Desktop development with C++
   - Or install Build Tools for Visual Studio separately

3. **WiX CLI v6.0+** (for installer)
   - Install using: [WiX Toolset v6.0.2](https://github.com/wixtoolset/wix/releases/tag/v6.0.2)
      - Install both: `wix-cli-x64.msi` and `WixAdditionalTools.exe`
   - The Visual Studio extension: [HeatWave](https://marketplace.visualstudio.com/items?itemName=FireGiant.FireGiantHeatWaveDev17)
   - [WiX Documentation](https://wixtoolset.org/docs/tools/)

4. **PowerShell 7 or later**
   - For PowerShell: [PowerShell Github](https://github.com/PowerShell/PowerShell/releases)

5. **FlashDevelop or IntelliJ IDEA Ultimate (for building Skua.AS3 project - skua.swf)**
   - **Option A - FlashDevelop**: [download](https://github.com/fdorg/flashdevelop/raw/refs/heads/development/Releases/FlashDevelop-5.3.3.exe)
   - **Option B - IntelliJ IDEA Ultimate**: [download](https://www.jetbrains.com/idea/download/)
     - Requires ActionScript & Flash plugin

### Optional Software

- **Git** for version control
- **GitHub CLI** for releases

## Quick Start

### Easiest Method: PowerShell Script

1. Clone the repository:

   ```bash
   git clone https://github.com/auqw/Skua.git
   cd Skua
   ```

2. Right-click `Build-Skua.ps1` and select "Run with PowerShell"
   - This builds the Release configuration for both `x64` and `x86`
   - Includes WiX installer creation
   - Output is placed in the `build` folder
   - **The window stays open after completion**, showing build results

## Build Scripts

### PowerShell Script (`Build-Skua.ps1`)

The main build automation script with full control over the build process.

#### Basic Usage

```powershell
# Build everything (x64, x86, installer)
.\Build-Skua.ps1

# Build specific configuration
.\Build-Skua.ps1 -Configuration Debug

# Build specific platforms
.\Build-Skua.ps1 -Platforms "x64"
.\Build-Skua.ps1 -Platforms "x86"

# Skip installer
.\Build-Skua.ps1 -SkipInstaller

# Skip cleaning
.\Build-Skua.ps1 -SkipClean

# Custom output path
.\Build-Skua.ps1 -OutputPath "C:\MyBuilds"
```

#### Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| Configuration | String | Release | Build configuration (Debug/Release) |
| Platforms | String | "x64", "x86" | Target platforms to build |
| SkipInstaller | Switch | $false | Skip building WiX installer |
| SkipClean | Switch | $false | Skip cleaning before build (faster incremental builds) |
| Parallel | Switch | $false | Build platforms and installers in parallel (~23% faster) |
| BinaryLog | Switch | $false | Generate .binlog files for build analysis |
| OutputPath | String | .\build | Output directory for artifacts |

#### Output Structure

```powershell
build/
├── Release/
│   ├── x64/
│   │   ├── Skua.App.WPF/
│   │   └── Skua.Manager/
│   └── x86/
│       ├── Skua.App.WPF/
│       └── Skua.Manager/
└── Installers/
    ├── Skua_Release_x64_Skua.Installer.msi
    └── Skua_Release_x86_Skua.Installer.msi
```

### Running the Build Script

The PowerShell script can be run in several ways:

1. **Right-click method**: Right-click `Build-Skua.ps1` → "Run with PowerShell"
2. **Command line**: Open PowerShell and run `.\Build-Skua.ps1`
3. **Batch files**: We have 3 next to the powershell script `Build.bat`, `Buildx64.bat`, and `Buildx64noInstaller.bat`
4. **Make your own**: Make a batch script with target:

   ```powershell
   powershell.exe -ExecutionPolicy Bypass -File "Build-Skua.ps1"
   ```

## Manual Building

### Using Visual Studio

1. Open `Skua.sln` in Visual Studio
2. Select configuration (Debug/Release) and platform (x64/x86)
3. Build → Build Solution (Ctrl+Shift+B)

### Using .NET CLI

```powershell
# Restore packages
dotnet restore

# Build x64 Release
dotnet build --configuration Release -p:Platform=x64

# Build x86 Release
dotnet build --configuration Release -p:Platform=x86

# Build specific project
dotnet build Skua.App.WPF\Skua.App.WPF.csproj --configuration Release
```

### Building skua.swf

`skua.swf` targets Flash Player 32.0. `playerglobal28_0.swc` can no longer be downloaded, so the build uses `playerglobal32_0.swc`.

- **Windows:** `Skua.AS3\compile-as3.ps1` runs `mxmlc` from your Flex SDK. The SDK needs `frameworks\libs\player\32.0\playerglobal.swc`: download [`playerglobal32_0.swc`](https://fpdownload.macromedia.com/get/flashplayer/updaters/32/playerglobal32_0.swc) (SHA-256 `7d4d6168d27603cfb3b750302448e354e0bbc1bdd58f5d101c3dcf6891e9bb65`) and save it there under that name.
- **macOS:** `Skua.AS3/compile-as3.sh` downloads Apache Flex SDK 4.16.1 and `playerglobal32_0.swc` itself, checks them against pinned SHA-256s, and caches them in `~/Library/Caches/skua-as3`. It needs Java (17 works).

Both scripts write `Skua.AS3/skua/bin/skua.swf` and print the SHA-256 of its `DoABC` tags. Compare builds by that hash, not the file hash: `mxmlc` writes a compile timestamp into every SWF.

### Building the macOS Engine and CLI

`Skua.MacOS.slnf` builds the headless Engine (`skua-engine`), the CLI (`skua`, which is also the MCP server as `skua mcp`) and their tests:

```bash
dotnet build Skua.MacOS.slnf
dotnet test Skua.MacOS.slnf
```

On macOS, building `Skua.App.Engine` also builds the Game Host and `skua.swf`, so it needs [Rust](https://rustup.rs) and Java (17 works); a missing `cargo` or `java` fails the build with an install hint.

- **`skua-gamehost`:** `cargo build --release --locked` in `Skua.GameHost/`. The first build takes about 5 minutes; after that it's a no-op of about a second.
- **`skua.swf`:** `Skua.AS3/compile-as3.sh`, rerun only when an AS3 source changes.
- **Escape hatches:** `-p:SkuaGameHostPath=<file>` uses a prebuilt `skua-gamehost` instead of running cargo, and `-p:SkuaSwfPath=<file>` a prebuilt `skua.swf` instead of running mxmlc.

The output is flat: `skua`, `skua-engine`, `skua-gamehost` and `skua.swf` sit side by side in `Skua.App.Engine/bin/<Configuration>/net10.0/` (and in `dotnet publish` output). `skua` auto-starts the `skua-engine` next to it, and the Engine starts the `skua-gamehost` and `skua.swf` next to itself. For MCP clients, the config is `{"command": "skua", "args": ["mcp"]}`. With a Homebrew .NET, set `DOTNET_ROOT` so the executables find the runtime.

The tests never run the real Game Host, read the real Keychain or reach AQW: they point `SKUA_GAMEHOST` at a fake that speaks the Bridge frames and simulates the game, `SKUA_SECURITY_TOOL` at a fake `security`, and `SKUA_AQ_SERVERS_URL` at a fake servers API.

Environment overrides:

| Variable | Overrides |
|---|---|
| `SKUA_DIR` | The Skua data folder (default `~/Library/Application Support/Skua`) |
| `SKUA_ENGINE` | The `skua-engine` that auto-start launches |
| `SKUA_ENGINE_SOCKET` | The Engine's socket (default `<SkuaDIR>/engines/default.sock`); the path must fit in 103 bytes |
| `SKUA_GAMEHOST`, `SKUA_SWF` | The Game Host the Engine runs, and the SWF it loads (default `skua-gamehost` and `skua.swf` next to the Engine); a missing file fails the start with an error naming its path |
| `SKUA_GITHUB_RAW_URL`, `SKUA_GITHUB_API_URL` | `https://raw.githubusercontent.com/` and `https://api.github.com/`, for tests |
| `SKUA_AQ_SERVERS_URL` | The game's servers API, `http://content.aq.com/game/api/data/servers`, for tests |
| `SKUA_SECURITY_TOOL` | The `security` tool that reads the Test Account from Keychain (default `/usr/bin/security`), for tests |

#### Test Account

`skua login [server]` (MCP `login`) logs the Test Account in and returns once it is playing; without a server it picks an online, non-member server with room, and `skua servers` lists them. It takes no credentials: the Engine reads the Test Account from Keychain, as the generic password under the service `skua-test-account`, whose account is the username. Add it once, and choose "Always Allow" when macOS asks whether `security` may read it (an unsigned rebuild may ask again):

```sh
security add-generic-password -s skua-test-account -a <username> -w   # asks for the password
```

To use another service, set `TestAccountService` under `client` in `<SkuaDIR>/Skua.settings.json`. The password and the game's `<pword>` login token are redacted from every log, event and log file. While logged in, the Engine holds off idle sleep (`pmset -g assertions` lists it) and keeps the lag killer on, lifting it for screenshots.

#### Moving and reading the game

Once playing, `skua join <map> [cell] [pad]` and `skua jump <cell> [pad]` (MCP `join`, `jump`) move the player and print where it ended up; `skua status` adds a player summary. `skua inventory [inventory|bank|temp|house]`, `skua quests [loaded|active]`, `skua map` and `skua drops` (MCP tools of the same names) read the game as DTOs; the bank is fetched from the game server the first time it is listed after each login. Anything else is for `eval`.

#### Script Source

`skua scripts update` (MCP `scripts_update`) syncs Scripts into `<SkuaDIR>/Scripts` from the Script Source, `auqw/Scripts@Skua` by default. The first sync from a Script Source downloads every Script; later ones download only the Scripts changed since the last synced commit. `skua scripts search <query> [--tag <tag>]` (MCP `scripts_search`) searches its `scripts.json`.

To use a fork, set `ScriptSource` under `shared` in `<SkuaDIR>/Skua.settings.json`, then run `skua engine stop`, since the Engine reads settings when it starts:

```json
{
  "shared": {
    "ScriptSource": { "Owner": "noelrohi", "Repo": "Scripts", "Branch": "Skua" }
  }
}
```

Script files always come from the Script Source itself, not from the `downloadUrl`s in `scripts.json`, which a fork keeps pointing at upstream.

### Building the Installer

Requires WiX CLI and MSBuild:

```powershell
# First install WiX CLI if not already installed
dotnet tool install --global wix

# Using MSBuild directly
msbuild Skua.Installer\Skua.Installer.wixproj /p:Configuration=Release /p:Platform=x64

# Or find MSBuild path first
"C:\Program Files\Microsoft Visual Studio\18\{EDITION}\MSBuild\Current\Bin\MSBuild.exe" ^
  Skua.Installer\Skua.Installer.wixproj ^
  /p:Configuration=Release ^
  /p:Platform=x64
```

## CI/CD

The `macOS` workflow builds `Skua.MacOS.slnf` (including `skua-gamehost` and `skua.swf`), runs the Game Host's `cargo test` and then the .NET tests, on every push to `master` and on every pull request. It caches cargo (rust-cache, saved only on `master`) and the Flex SDK downloads (`~/Library/Caches/skua-as3`, keyed on `compile-as3.sh`).

### Local CI Testing

Test the build process locally before pushing:

```powershell
# Full build test
.\Build-Skua.ps1 -Configuration Release

# Debug build test
.\Build-Skua.ps1 -Configuration Debug -Platforms "x64"
```

## Troubleshooting

### Common Issues

#### WiX CLI Not Found

- **Error**: "WiX CLI v6+ not found"
- **Solution**: Install WiX CLI using: `dotnet tool install --global wix` or [get it here](https://github.com/wixtoolset/wix/releases/tag/v6.0.2)
- **Verify**: Run `wix --version` to confirm installation

#### MSBuild Not Found

- **Error**: "MSBuild not found"
- **Solution**: Install Visual Studio or Build Tools for Visual Studio
- Alternative: Use Developer Command Prompt

#### NuGet Restore Failures

```powershell
# Clear NuGet cache
dotnet nuget locals all --clear

# Restore with verbose output
dotnet restore --verbosity detailed
```

#### Platform Build Issues

```powershell
# Ensure platform is specified correctly
dotnet build -p:Platform=x64  # Not "--platform x64"

# For x86, may need explicit target
dotnet build -p:Platform=x86 -p:PlatformTarget=x86
```

#### Permission Errors

- Run PowerShell as Administrator if needed
- Check execution policy: `Get-ExecutionPolicy`
- Temporarily bypass: `powershell -ExecutionPolicy Bypass -File Build-Skua.ps1`

#### Optimization Tips

1. **Parallel builds**: Use `-Parallel` flag to build platforms and installers simultaneously
2. **Incremental builds**: Use `-SkipClean` when testing (fastest for development)
3. **Skip installer**: Use `-SkipInstaller` during development
4. **Binary logging**: Use `-BinaryLog` to analyze build performance with MSBuild Structured Log Viewer

#### Recommended Build Commands

```powershell
# Daily development (fastest)
.\Build-Skua.ps1 -Parallel -SkipClean -SkipInstaller

# Full build for release
.\Build-Skua.ps1 -Parallel

# Debug and analyze build
.\Build-Skua.ps1 -BinaryLog
```

### Validation

Verify your build:

```powershell
# Check output files exist
Get-ChildItem -Path build -Recurse -Include *.exe, *.dll, *.msi

# Test the application
.\build\Release\x64\Skua.App.WPF\Skua.exe

# Verify installer
msiexec /i "build\Installers\Skua_Release_x64_Skua.Installer.msi" /quiet
```

## Advanced Configuration

### Build Version Management

Versions are centrally managed in `Directory.Build.props` at the repository root:

```xml
<!-- Directory.Build.props -->
<?xml version="1.0" encoding="utf-8"?>
<Project>
  <PropertyGroup>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
    <FileVersion>1.0.0.0</FileVersion>
    <Version>1.0.0.0</Version>
  </PropertyGroup>
</Project>
```

## Contributing

When contributing build system changes:

1. Test all platforms (x64, x86)
2. Test both Debug and Release configurations
3. Verify the installer builds correctly
4. Update this documentation if needed
5. Test GitHub Actions workflow locally if possible

## Support

For build issues:

1. Check [Prerequisites](#prerequisites) are installed
2. Review [Troubleshooting](#troubleshooting) section
3. Check existing GitHub issues
4. Create a new issue with:
   - Build error messages
   - System information (Windows version, .NET version)
   - Steps to reproduce
