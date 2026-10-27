<#
.SYNOPSIS
  Builds Xcord (bridge + Game Bar widget).

.DESCRIPTION
  Default (Debug): publishes the bridge, builds the widget and registers it on this PC
  (needs Developer Mode; no signing).

  -Release: .NET Native build, signed with a local self-signed certificate. The output folder
  AppPackages\Xcord.Widget_<ver>_x64_Test is what you copy to the ROG Ally: run Install.ps1 there
  (it trusts the certificate and installs the dependencies).

.EXAMPLE
  .\build.ps1
  .\build.ps1 -Release
#>
param(
    [switch]$Release,
    [switch]$NoDeploy
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$widget = Join-Path $root 'src\Xcord.Widget'
$config = if ($Release) { 'Release' } else { 'Debug' }

$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * `
    -requires Microsoft.VisualStudio.Workload.UniversalBuildTools Microsoft.VisualStudio.Workload.Universal `
    -requiresAny -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { $msbuild = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" }
if (-not (Test-Path $msbuild)) { throw 'MSBuild with the UWP workload not found (install VS Build Tools + "Universal Windows Platform build tools").' }

Write-Host "==> Publishing bridge" -ForegroundColor Cyan
$bridgeOut = Join-Path $widget 'Bridge'
if (Test-Path $bridgeOut) { Remove-Item $bridgeOut -Recurse -Force }
dotnet publish (Join-Path $root 'src\Xcord.Bridge') -c Release -r win-x64 -o $bridgeOut -nologo -v q
if ($LASTEXITCODE) { throw 'Bridge publish failed' }

$msbuildArgs = @(
    (Join-Path $widget 'Xcord.Widget.csproj'), '/restore', "/p:Configuration=$config", '/p:Platform=x64',
    '/p:UapAppxPackageBuildMode=SideloadOnly', '/v:minimal', '/nologo'
)

if ($Release) {
    # Self-signed cert whose subject matches the manifest Publisher (CN=lopesleo).
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=lopesleo' -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
    if (-not $cert) {
        Write-Host "==> Creating signing certificate CN=lopesleo" -ForegroundColor Cyan
        $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=lopesleo' -KeyUsage DigitalSignature `
            -FriendlyName 'Xcord dev signing' -CertStoreLocation Cert:\CurrentUser\My `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    }
    $msbuildArgs += '/p:AppxPackageSigningEnabled=true', "/p:PackageCertificateThumbprint=$($cert.Thumbprint)"
}

Write-Host "==> Building widget ($config)" -ForegroundColor Cyan
& $msbuild @msbuildArgs
if ($LASTEXITCODE) { throw 'Widget build failed' }

$pkgDir = Get-ChildItem (Join-Path $widget 'AppPackages') -Directory |
    Where-Object { $_.Name -like "*_x64_$(if ($Release) { '' } else { 'Debug_' })Test" } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$msix = Get-ChildItem $pkgDir.FullName -Filter *.msix | Select-Object -First 1
Write-Host "Package: $($msix.FullName)" -ForegroundColor Green

if ($Release) {
    # Zip the sideload folder (msix + cert + Install.ps1 + dependencies) for a GitHub release.
    $version = ([xml](Get-Content (Join-Path $widget 'Package.appxmanifest'))).Package.Identity.Version -replace '\.0$', ''
    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Force $dist | Out-Null
    $zip = Join-Path $dist "Xcord-$version-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $pkgDir.FullName '*') -DestinationPath $zip
    Write-Host "Release zip: $zip" -ForegroundColor Green
    return
}
if ($NoDeploy) { return }

Write-Host "==> Registering on this PC" -ForegroundColor Cyan
foreach ($dep in Get-ChildItem (Join-Path $pkgDir.FullName 'Dependencies\x64') -Filter *.appx) {
    try { Add-AppxPackage -Path $dep.FullName -ErrorAction Stop } catch { } # already installed (same or newer)
}

# Register the unpacked layout: Developer Mode accepts it unsigned. Re-registering in place keeps
# LocalState (the Discord token); removing the package would wipe it.
$layout = Join-Path $widget 'obj\layout'
for ($attempt = 1; Test-Path $layout; $attempt++) {
    # A pinned widget gets relaunched by Game Bar right after being killed, so retry a few times.
    $running = Get-Process Xcord.Widget, Xcord.Bridge -ErrorAction SilentlyContinue
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    try { Remove-Item $layout -Recurse -Force -ErrorAction Stop }
    catch {
        if ($attempt -ge 5) { throw 'Xcord is still running (pinned in Game Bar?). Close/unpin the widget and run again.' }
        Start-Sleep -Milliseconds 500
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($msix.FullName, $layout)

Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml') -ForceApplicationShutdown -ForceUpdateFromAnyVersion
Write-Host "Done. Open Game Bar (Win+G) -> Widgets -> Xcord." -ForegroundColor Green
