$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

$coreDll = Join-Path $scriptDir "Microsoft.Web.WebView2.Core.dll"
$winFormsDll = Join-Path $scriptDir "Microsoft.Web.WebView2.WinForms.dll"
$loaderDll = Join-Path $scriptDir "WebView2Loader.dll"

if (-not (Test-Path $coreDll) -or -not (Test-Path $winFormsDll) -or -not (Test-Path $loaderDll)) {
    Write-Host "Fetching official Microsoft.Web.WebView2 managed wrapper (~2 MB)..." -ForegroundColor Cyan
    $nupkgZip = Join-Path $scriptDir "webview2_pkg.zip"
    $extractDir = Join-Path $scriptDir "webview2_tmp"

    Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2" -OutFile $nupkgZip -UseBasicParsing
    if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
    Expand-Archive -Path $nupkgZip -DestinationPath $extractDir -Force

    # Locate net462 or net45 managed assemblies
    $netLibDir = Join-Path $extractDir "lib\net462"
    if (-not (Test-Path $netLibDir)) {
        $netLibDir = (Get-ChildItem (Join-Path $extractDir "lib") -Directory | Select-Object -First 1).FullName
    }

    Copy-Item (Join-Path $netLibDir "Microsoft.Web.WebView2.Core.dll") $coreDll -Force
    Copy-Item (Join-Path $netLibDir "Microsoft.Web.WebView2.WinForms.dll") $winFormsDll -Force
    Copy-Item (Join-Path $extractDir "runtimes\win-x64\native\WebView2Loader.dll") $loaderDll -Force

    Remove-Item $nupkgZip -Force -ErrorAction SilentlyContinue
    Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "WebView2 libraries ready." -ForegroundColor Green
}

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    throw "C# compiler not found at $csc"
}

Write-Host "Compiling WingetUI.exe..." -ForegroundColor Cyan

# Stop existing instance if running so we can overwrite WingetUI.exe
Get-Process -Name "WingetUI" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

& $csc /nologo /target:winexe /platform:x64 /optimize+ `
    /out:"$scriptDir\WingetUI.exe" `
    /r:"System.dll" `
    /r:"System.Core.dll" `
    /r:"System.Drawing.dll" `
    /r:"System.Windows.Forms.dll" `
    /r:"System.Web.Extensions.dll" `
    /r:"$coreDll" `
    /r:"$winFormsDll" `
    "$scriptDir\WingetUI.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}
Write-Host "Build succeeded: $scriptDir\WingetUI.exe" -ForegroundColor Green

# ---- Portable package: dist\WingetBaby\ (+ zip). Copy the folder anywhere; state lives in its Data\ subfolder.
$dist = Join-Path $scriptDir "dist\WingetBaby"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist -Force | Out-Null

Copy-Item "$scriptDir\WingetUI.exe" $dist
Copy-Item $coreDll, $winFormsDll, $loaderDll $dist
Copy-Item "$scriptDir\ui" (Join-Path $dist "ui") -Recurse
Copy-Item "$scriptDir\presets.json" $dist   # seed only; the app copies it to Data\ on first run

$zip = Join-Path $scriptDir "dist\WingetBaby-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $dist -DestinationPath $zip
Write-Host "Portable package: $dist" -ForegroundColor Green
Write-Host "Portable zip:     $zip" -ForegroundColor Green
