# AS3 Compilation Helper Script
# This script attempts to compile the Skua AS3 client using available compilers

param(
    [switch]$DownloadSDK,
    [string]$SDKPath = "."
)

# Hashes the SWF's DoABC tags, which identify a build. The file hash changes on every build because
# mxmlc writes a compile timestamp into the ProductInfo tag.
function Get-DoAbcSha256([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $signature = [System.Text.Encoding]::ASCII.GetString($bytes, 0, 3)
    $body = [System.IO.MemoryStream]::new()
    if ($signature -eq "CWS") {
        $zlib = [System.IO.Compression.ZLibStream]::new(
            [System.IO.MemoryStream]::new($bytes, 8, $bytes.Length - 8),
            [System.IO.Compression.CompressionMode]::Decompress)
        $zlib.CopyTo($body)
        $zlib.Dispose()
    } elseif ($signature -eq "FWS") {
        $body.Write($bytes, 8, $bytes.Length - 8)
    } else {
        throw "Not an uncompressed or zlib SWF: $signature"
    }
    $data = $body.ToArray()

    # Skip the frame RECT, frame rate and frame count.
    $pos = [int][Math]::Floor((5 + 4 * ($data[0] -shr 3) + 7) / 8) + 4
    $abc = [System.IO.MemoryStream]::new()
    while ($pos -lt $data.Length) {
        $header = [BitConverter]::ToUInt16($data, $pos)
        $pos += 2
        $code = $header -shr 6
        $length = $header -band 0x3f
        if ($length -eq 0x3f) {
            $length = [BitConverter]::ToInt32($data, $pos)
            $pos += 4
        }
        # DoABC, DoABC2
        if ($code -eq 72 -or $code -eq 82) { $abc.Write($data, $pos, $length) }
        $pos += $length
    }
    if ($abc.Length -eq 0) { throw "No DoABC tag in $Path" }

    $abc.Position = 0
    return (Get-FileHash -InputStream $abc -Algorithm SHA256).Hash.ToLowerInvariant()
}

Write-Host "Skua AS3 Compilation Helper" -ForegroundColor Cyan
Write-Host "================================" -ForegroundColor Cyan

# Check for ActionScript project
$asProjectPath = "skua\skua.as3proj"
$asconfigPath = "skua\asconfig.json"
$mainSourcePath = "skua\src\skua\Main.as"
$outputPath = "skua\bin\skua.swf"

if (-not (Test-Path $mainSourcePath)) {
    Write-Host "❌ Main.as source file not found: $mainSourcePath" -ForegroundColor Red
    exit 1
}

Write-Host "✅ Found AS3 source files" -ForegroundColor Green

# Try different compilation methods

# Method 1: Check for mxmlc (Flex SDK)
Write-Host "`n🔍 Checking for Flex SDK compiler..." -ForegroundColor Yellow
$mxmlcPath = Get-Command mxmlc -ErrorAction SilentlyContinue
if ($mxmlcPath) {
    Write-Host "✅ Found mxmlc at: $($mxmlcPath.Source)" -ForegroundColor Green
    
    # Compile using mxmlc
    Write-Host "🔧 Compiling with mxmlc..." -ForegroundColor Yellow
    & mxmlc -source-path "skua\src" -default-size 958 550 -output $outputPath "skua\src\skua\Main.as" -target-player 32.0 -optimize
    
    if ($LASTEXITCODE -eq 0 -and (Test-Path $outputPath)) {
        Write-Host "✅ Compilation successful! Output: $outputPath" -ForegroundColor Green
        Write-Host "📁 SWF size: $((Get-Item $outputPath).Length) bytes" -ForegroundColor Green
        Write-Host "🔑 DoABC sha256: $(Get-DoAbcSha256 (Resolve-Path $outputPath).Path)" -ForegroundColor Green
        exit 0
    } else {
        Write-Host "❌ Compilation failed with mxmlc" -ForegroundColor Red
    }
}

# Method 2: Check for asconfigc (ActionScript & MXML extension for VS Code)
Write-Host "`n🔍 Checking for asconfigc..." -ForegroundColor Yellow
$asconfigcPath = Get-Command asconfigc -ErrorAction SilentlyContinue
if ($asconfigcPath) {
    Write-Host "✅ Found asconfigc at: $($asconfigcPath.Source)" -ForegroundColor Green
    
    if (Test-Path $asconfigPath) {
        Write-Host "🔧 Compiling with asconfigc..." -ForegroundColor Yellow
        Push-Location "skua"
        & asconfigc
        Pop-Location
        
        if ($LASTEXITCODE -eq 0 -and (Test-Path $outputPath)) {
            Write-Host "✅ Compilation successful! Output: $outputPath" -ForegroundColor Green
            Write-Host "📁 SWF size: $((Get-Item $outputPath).Length) bytes" -ForegroundColor Green
            Write-Host "🔑 DoABC sha256: $(Get-DoAbcSha256 (Resolve-Path $outputPath).Path)" -ForegroundColor Green
            exit 0
        } else {
            Write-Host "❌ Compilation failed with asconfigc" -ForegroundColor Red
        }
    }
}

# Method 3: Check for Royale SDK
Write-Host "`n🔍 Checking for Apache Royale SDK..." -ForegroundColor Yellow
$royalePath = Get-Command asjsc -ErrorAction SilentlyContinue
if ($royalePath) {
    Write-Host "[SUCCESS] Found Royale SDK at: $($royalePath.Source)" -ForegroundColor Green
    Write-Host "[INFO] Note: Royale compiles to HTML/JS, not SWF. Skipping." -ForegroundColor Yellow
}

# Method 4: Check for Adobe Animate
Write-Host "`n🔍 Checking for Adobe Animate..." -ForegroundColor Yellow
$animatePath = @(
    "${env:ProgramFiles}\Adobe\Adobe Animate*\Animate.exe",
    "${env:ProgramFiles(x86)}\Adobe\Adobe Animate*\Animate.exe"
) | Get-ChildItem -ErrorAction SilentlyContinue | Select-Object -First 1

if ($animatePath) {
    Write-Host "[SUCCESS] Found Adobe Animate at: $($animatePath.FullName)" -ForegroundColor Green
    Write-Host "[INFO] Note: Adobe Animate requires manual compilation. Open the .as3proj file in Animate and publish." -ForegroundColor Yellow
}

# SDK Download option
if ($DownloadSDK) {
    Write-Host "`n[INFO] Downloading Flex SDK..." -ForegroundColor Yellow
    Write-Host "[INFO] This feature is not implemented yet. Please manually download:" -ForegroundColor Yellow
    Write-Host "   • Apache Flex SDK: https://flex.apache.org/download-binaries.html" -ForegroundColor White
    Write-Host "   • Adobe AIR SDK: https://airsdk.harman.com/download" -ForegroundColor White
}

# No compiler found
Write-Host "`n❌ No ActionScript compiler found!" -ForegroundColor Red
Write-Host "To compile the AS3 code, you need one of:" -ForegroundColor Yellow
Write-Host "   • Adobe Flex SDK with mxmlc compiler" -ForegroundColor White
Write-Host "   • Adobe AIR SDK" -ForegroundColor White
Write-Host "   • ActionScript & MXML extension for VS Code with asconfigc" -ForegroundColor White
Write-Host "   • Adobe Animate CC" -ForegroundColor White

Write-Host "`n🔧 Installation suggestions:" -ForegroundColor Yellow
Write-Host "   npm install -g @apache-flex/flex-sdk" -ForegroundColor White
Write-Host "   npm install -g asconfigc" -ForegroundColor White
Write-Host "   Or download SDK manually from: https://flex.apache.org" -ForegroundColor White

exit 1
