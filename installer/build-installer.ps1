$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$portableDir = Join-Path $repoRoot "publish\win-x64"
$appDir = Join-Path $repoRoot "publish\win-x64-app"
$installerScript = Join-Path $PSScriptRoot "Gem.iss"

Get-Process Gem -ErrorAction SilentlyContinue | ForEach-Object {
    $_ | Stop-Process -Force
    $_.WaitForExit(5000) | Out-Null
}

foreach ($dir in @($portableDir, $appDir)) {
    if (Test-Path $dir) {
        Remove-Item $dir -Recurse -Force
    }
}

# 1) Unpacked layout for the Windows installer (maps DLLs directly from disk to minimize private RAM)
dotnet publish (Join-Path $repoRoot "src\Gem\Gem.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $appDir `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish (installer app layout) failed with exit code $LASTEXITCODE"
}

# 2) Portable single-file executable
dotnet publish (Join-Path $repoRoot "src\Gem\Gem.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $portableDir `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish (portable single-file) failed with exit code $LASTEXITCODE"
}

$isccCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}

if (-not $iscc) {
    throw "Inno Setup 6 (ISCC.exe) was not found. Install it via: winget install JRSoftware.InnoSetup"
}

& $iscc $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe failed with exit code $LASTEXITCODE"
}

[xml]$csproj = Get-Content (Join-Path $repoRoot "src\Gem\Gem.csproj")
$appVersion = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $appVersion) { $appVersion = "1.0.1" }
$zipOut = Join-Path $repoRoot "publish\installer\Gem-v$appVersion-unpack-win-amd64.zip"
if (Test-Path $zipOut) { Remove-Item $zipOut -Force }
Compress-Archive -Path (Join-Path $appDir "*") -DestinationPath $zipOut -CompressionLevel Optimal -Force
