# GravityCapture desktop cleanup helper
#
# Purpose: Fix the most common cause of duplicate-definition WPF build errors
# (CS0101/CS0102/CS0111/CS8646) after extracting a patch ZIP over an existing folder.
#
# Run this script from: src/GravityCapture
#   powershell -ExecutionPolicy Bypass -File .\CLEANUP_IF_BUILD_ERRORS.ps1

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$projDir = Join-Path $here 'GravityCapture'

if (-not (Test-Path $projDir)) {
  Write-Error "Project directory not found: $projDir"
  exit 1
}

Write-Host "[1/4] Deleting bin/ and obj/ ..." -ForegroundColor Cyan
Get-ChildItem -Path $projDir -Directory -Force -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -in @('bin','obj') } |
  ForEach-Object {
    Write-Host "  removing $($_.FullName)" -ForegroundColor DarkGray
    Remove-Item -Recurse -Force -LiteralPath $_.FullName -ErrorAction SilentlyContinue
  }

Write-Host "[2/4] Removing any stray generated WPF files in the project tree (should only live under obj/) ..." -ForegroundColor Cyan
Get-ChildItem -Path $projDir -Recurse -Force -File -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -match '\.g(\.i)?\.cs$' } |
  ForEach-Object {
    # If generated files somehow got checked into the project folder, delete them.
    # (Legit generated files are under obj/, which we already removed.)
    Write-Host "  deleting $($_.FullName)" -ForegroundColor DarkGray
    Remove-Item -Force -LiteralPath $_.FullName -ErrorAction SilentlyContinue
  }

Write-Host "[3/4] Removing common leftover files from older desktop attempts ..." -ForegroundColor Cyan
$knownLeftovers = @(
  (Join-Path $projDir 'ApiClient.cs'),
  (Join-Path $projDir 'AppSettings.cs'),
  (Join-Path $projDir 'SettingsStore.cs'),
  (Join-Path $projDir 'ScreenCaptureService.cs')
)
foreach ($p in $knownLeftovers) {
  if (Test-Path $p) {
    Write-Host "  deleting leftover: $p" -ForegroundColor DarkGray
    Remove-Item -Force -LiteralPath $p -ErrorAction SilentlyContinue
  }
}

Write-Host "[4/4] Detecting duplicates (you should see exactly ONE path per item) ..." -ForegroundColor Cyan
$check = @('App.xaml','MainWindow.xaml','ApiClient.cs','AppSettings.cs')
foreach ($name in $check) {
  $hits = Get-ChildItem -Path $projDir -Recurse -Force -File -Filter $name -ErrorAction SilentlyContinue
  if ($hits.Count -eq 0) {
    Write-Host "  $name: (none found)" -ForegroundColor Yellow
  } elseif ($hits.Count -eq 1) {
    Write-Host "  $name: OK -> $($hits[0].FullName)" -ForegroundColor Green
  } else {
    Write-Host "  $name: DUPLICATES FOUND ($($hits.Count))" -ForegroundColor Red
    $hits | ForEach-Object { Write-Host "    $($_.FullName)" -ForegroundColor Red }
  }
}

Write-Host "\nDone. Re-open the solution and Build." -ForegroundColor Cyan
