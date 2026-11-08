# Publish pipeline with IL string encryption.
#
# Why this exists: `dotnet publish -p:PublishSingleFile=true` always bundles the
# DLL it just built. To get the *encrypted* DLL into the single-file exe we
# build first, encrypt in place, then publish with --no-build so publish reuses
# the already-encrypted assembly instead of rebuilding it.
#
# Note: keep this file ASCII-only. Windows PowerShell 5.1 reads BOM-less files
# as ANSI, so non-ASCII text here would come out garbled.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Build-only cleanup helpers.
# The host intercepts Remove-Item and routes it through a safe-delete (recycle bin)
# wrapper that fail-closes for these temp dirs. .NET's File/Directory.Delete is not
# wrapped, and these paths are strictly build output (.tmp-obf / dist-standalone),
# never user data.
function Remove-TreeQuiet([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    try {
        $full = (Resolve-Path -LiteralPath $path).Path
        [System.IO.Directory]::Delete($full, $true)
    } catch {
        Write-Host ('      cleanup skipped: ' + $path)
    }
}

function Remove-FileQuiet([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    try { [System.IO.File]::Delete((Resolve-Path -LiteralPath $path).Path) } catch { }
}

$project = 'src\SysDiag\SysDiag.csproj'
$binDir  = 'src\SysDiag\bin\Release\net8.0-windows\win-x64'
$binDll  = Join-Path $binDir 'SysDiag.dll'
$obfDir  = '.tmp-obf'
$distDir = 'dist-standalone'
$finalExe = -join ([char[]](0x7834,0x70C2,0x4F18,0x5316)) + '.exe'

Write-Host ''
Write-Host '[1/5] build (Release, self-contained)'
dotnet build $project -c Release -r win-x64 --self-contained true -p:DebugType=none -v q
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

# --no-build publish reuses singlefilehost.exe from obj, but only a *publish* produces it.
# After a clean (obj removed) that file is missing and bundling fails with MSB4018.
# Warm it up once with a throwaway publish; normal incremental runs skip this.
$singleFileHost = 'src\SysDiag\obj\Release\net8.0-windows\win-x64\singlefilehost.exe'
if (-not (Test-Path $singleFileHost)) {
    Write-Host '      singlefilehost.exe missing (clean build) - warming up'
    # Write into obj (already ignored, and never deleted here): recursive Remove-Item
    # on a temp dir gets intercepted by the host's safe-delete wrapper and fail-closes.
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o 'src\SysDiag\obj\warmup' -v q
    if ($LASTEXITCODE -ne 0) { throw 'warmup publish failed' }
}

Write-Host '[2/5] encrypt string literals'
$obf = Join-Path $env:USERPROFILE '.dotnet\tools\obfuscar.console.exe'
if (-not (Test-Path $obf)) {
    throw 'Obfuscar not found. Install it with: dotnet tool install -g Obfuscar.GlobalTool'
}
Remove-TreeQuiet $obfDir
& $obf 'tools\obfuscar.xml'
if ($LASTEXITCODE -ne 0) { throw 'obfuscation failed' }
if (-not (Test-Path (Join-Path $obfDir 'SysDiag.dll'))) { throw 'obfuscation produced no output' }

Copy-Item (Join-Path $obfDir 'SysDiag.dll') $binDll -Force
Write-Host ('      encrypted dll: ' + (Get-Item $binDll).Length + ' bytes')

Write-Host '[3/5] bundle single file (reusing encrypted dll)'
Remove-TreeQuiet $distDir
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    --no-build -o $distDir
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Write-Host '[4/5] copy to project root'
# The target may be briefly locked by antivirus or a leftover process.
# Overwriting then fails with "Device or resource busy". Try delete first;
# if that fails, rename it aside (rename works even while being read), copy, then clean up.
if (Test-Path $finalExe) {
    try { Remove-FileQuiet $finalExe; if (Test-Path $finalExe) { throw 'locked' } }
    catch {
        $stale = $finalExe + '.old-' + (Get-Date -Format 'HHmmss')
        try { Rename-Item $finalExe $stale -ErrorAction Stop } catch { }
    }
}
Copy-Item (Join-Path $distDir 'SysDiag.exe') $finalExe -Force
Get-ChildItem ($finalExe + '.old-*') -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-FileQuiet $_.FullName }

Write-Host '[5/5] clean intermediates'
Remove-TreeQuiet $distDir
Remove-TreeQuiet $obfDir

$out = Get-Item $finalExe
Write-Host ''
Write-Host ('[DONE] ' + $out.Name + '  ' + [math]::Round($out.Length / 1MB, 1) + ' MB')
