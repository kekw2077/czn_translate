<#
.SYNOPSIS
    Bundles the offline translation-station conveyor into a deploy folder so the app can translate
    through a workstation with no system Python installed.

.DESCRIPTION
    Copies the stdlib-only pieces of the Python conveyor (station_fill.py + czn.segment + czn.station)
    into <Deploy>\tools, and drops a Windows embeddable Python into <Deploy>\runtime\python with its
    ._pth pointed at the tools folder. station_fill.py imports nothing outside the standard library,
    so no pip install is ever needed.

    Idempotent: re-running refreshes the scripts and leaves an already-present runtime alone.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\bundle-station.ps1 -Deploy "C:\Files\CZN Translator"
#>
param(
    [string]$Deploy = "C:\Files\CZN Translator",
    [string]$PythonVersion = "3.12.7"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

# --- 1. the stdlib-only conveyor -------------------------------------------------
$toolsSrc = Join-Path $repo "tools"
$toolsDst = Join-Path $Deploy "tools"
$cznDst = Join-Path $toolsDst "czn"
New-Item -ItemType Directory -Force -Path $cznDst | Out-Null

foreach ($s in @("station_fill.py", "station_probe.py")) {
    Copy-Item (Join-Path $toolsSrc $s) $toolsDst -Force
}
foreach ($m in @("__init__.py", "segment.py", "station.py")) {
    Copy-Item (Join-Path $toolsSrc "czn\$m") $cznDst -Force
}
Write-Host "tools -> $toolsDst (station_fill.py + station_probe.py + czn.segment/station)"

# --- 2. the embeddable Python runtime -------------------------------------------
$runtime = Join-Path $Deploy "runtime\python"
$pythonExe = Join-Path $runtime "python.exe"

if (-not (Test-Path $pythonExe)) {
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    $zip = Join-Path $env:TEMP "python-$PythonVersion-embed-amd64.zip"
    if (-not (Test-Path $zip)) {
        $url = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip"
        Write-Host "downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing -TimeoutSec 180
    }
    Expand-Archive -Path $zip -DestinationPath $runtime -Force
    Write-Host "runtime -> $runtime (Python $PythonVersion embeddable)"
} else {
    Write-Host "runtime already present -> $runtime"
}

# --- 3. point the runtime's path file at ..\..\tools -----------------------------
# The embeddable ._pth fully controls sys.path (and disables PYTHONPATH), so the tools folder has to
# be listed here for 'import czn.segment' to resolve.
$pthFile = Get-ChildItem -Path $runtime -Filter "python*._pth" | Select-Object -First 1
if ($pthFile) {
    $lines = Get-Content $pthFile.FullName
    if ($lines -notcontains "..\..\tools") {
        Add-Content -Path $pthFile.FullName -Value "..\..\tools" -Encoding ascii
        Write-Host "patched $($pthFile.Name): added ..\..\tools"
    } else {
        Write-Host "$($pthFile.Name) already lists ..\..\tools"
    }
}

# --- 4. smoke test ---------------------------------------------------------------
& $pythonExe (Join-Path $toolsDst "station_fill.py") "--help" *> $null
if ($LASTEXITCODE -eq 0) {
    Write-Host "OK: runtime can import and run station_fill.py"
} else {
    Write-Warning "station_fill.py --help exited $LASTEXITCODE - check the runtime"
}
