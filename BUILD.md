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
   - Project targets: `net10.0-windows` for applications and libraries

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

- **Windows:** `Skua.AS3\compile-as3.ps1` runs `mxmlc` from your Flex SDK. The SDK needs `frameworks\libs\player\32.0\playerglobal.swc`.
- **macOS:** `Skua.AS3/compile-as3.sh` downloads Apache Flex SDK 4.16.1 and `playerglobal32_0.swc` itself, checks them against pinned SHA-256s, and caches them in `~/Library/Caches/skua-as3`. It needs Java (17 works).

Both scripts write `Skua.AS3/skua/bin/skua.swf` and print the SHA-256 of its `DoABC` tags. Compare builds by that hash, not the file hash: `mxmlc` writes a compile timestamp into every SWF.

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
