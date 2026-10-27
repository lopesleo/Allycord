<#
.SYNOPSIS
  Builds Allycord (bridge + Game Bar widget).

.DESCRIPTION
  Default (Debug): publishes the bridge, builds the widget and registers it on this PC
  (needs Developer Mode; no signing).

  -Release: .NET Native build, signed with a local self-signed certificate. The output folder
  AppPackages\Allycord.Widget_<ver>_x64_Test is what you copy to the ROG Ally: run Install.ps1 there
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
$widget = Join-Path $root 'src\Allycord.Widget'
$config = if ($Release) { 'Release' } else { 'Debug' }

$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * `
    -requires Microsoft.VisualStudio.Workload.UniversalBuildTools Microsoft.VisualStudio.Workload.Universal `
    -requiresAny -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { $msbuild = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" }
if (-not (Test-Path $msbuild)) { throw 'MSBuild with the UWP workload not found (install VS Build Tools + "Universal Windows Platform build tools").' }

Write-Host "==> Publishing bridge" -ForegroundColor Cyan
$bridgeOut = Join-Path $widget 'Bridge'
if (Test-Path $bridgeOut) { Remove-Item $bridgeOut -Recurse -Force }
dotnet publish (Join-Path $root 'src\Allycord.Bridge') -c Release -r win-x64 -o $bridgeOut -nologo -v q
if ($LASTEXITCODE) { throw 'Bridge publish failed' }

$msbuildArgs = @(
    (Join-Path $widget 'Allycord.Widget.csproj'), '/restore', "/p:Configuration=$config", '/p:Platform=x64',
    '/p:UapAppxPackageBuildMode=SideloadOnly', '/v:minimal', '/nologo'
)

if ($Release) {
    # Self-signed cert whose subject matches the manifest Publisher (CN=lopesleo).
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=lopesleo' -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
    if (-not $cert) {
        Write-Host "==> Creating signing certificate CN=lopesleo" -ForegroundColor Cyan
        $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=lopesleo' -KeyUsage DigitalSignature `
            -FriendlyName 'Allycord dev signing' -CertStoreLocation Cert:\CurrentUser\My `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    }
    $msbuildArgs += '/p:AppxPackageSigningEnabled=true', "/p:PackageCertificateThumbprint=$($cert.Thumbprint)",
        # Don't ship Visual Studio's install-script telemetry to users.
        '/p:AppxLogTelemetryFromSideloadingScript=false'
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
    $zip = Join-Path $dist "Allycord-$version-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip }

    # Only what an x64 install needs: no other-architecture dependencies, symbols or telemetry.
    $stage = Join-Path $dist 'stage'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    Copy-Item $pkgDir.FullName $stage -Recurse
    Get-ChildItem (Join-Path $stage 'Dependencies') -Directory | Where-Object Name -ne 'x64' | Remove-Item -Recurse -Force
    Get-ChildItem $stage -Filter *.appxsym | Remove-Item -Force
    Remove-Item (Join-Path $stage 'TelemetryDependencies') -Recurse -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    Remove-Item $stage -Recurse -Force
    # install.ps1 refuses releases without this.
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    Set-Content -Path "$zip.sha256" -Value "$hash  $(Split-Path $zip -Leaf)" -Encoding ascii -NoNewline
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
    $running = Get-Process Allycord.Widget, Allycord.Bridge -ErrorAction SilentlyContinue
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    try { Remove-Item $layout -Recurse -Force -ErrorAction Stop }
    catch {
        if ($attempt -ge 5) { throw 'Allycord is still running (pinned in Game Bar?). Close/unpin the widget and run again.' }
        Start-Sleep -Milliseconds 500
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($msix.FullName, $layout)

$manifest = Join-Path $layout 'AppxManifest.xml'
$needsReinstall = $false
try {
    Add-AppxPackage -Register $manifest -ForceApplicationShutdown -ForceUpdateFromAnyVersion -ErrorAction Stop
    # Registering the same version from another folder can "succeed" while the old folder stays live.
    $needsReinstall = (Get-AppxPackage -Name 'lopesleo.Allycord').InstallLocation -ne (Resolve-Path $layout).Path
}
catch {
    # 0x80073CFB: manifest changed but the version didn't.
    if ($_.Exception.HResult -ne -2147009285 -and $_.Exception.Message -notmatch '80073CFB') { throw }
    $needsReinstall = $true
}
if ($needsReinstall) {
    # Reinstall, carrying LocalState (token, settings) over.
    $pkg = Get-AppxPackage -Name 'lopesleo.Allycord'
    $state = "$env:LOCALAPPDATA\Packages\$($pkg.PackageFamilyName)\LocalState"
    # LocalState files carry the package's encryption; read the bytes instead of Copy-Item.
    $saved = @{}
    foreach ($name in 'settings.json', 'discord_token.json') {
        $file = Join-Path $state $name
        if (Test-Path $file) { $saved[$name] = [System.IO.File]::ReadAllBytes($file) }
    }
    $pkg | Remove-AppxPackage
    Add-AppxPackage -Register $manifest -ForceApplicationShutdown
    New-Item -ItemType Directory -Force $state | Out-Null
    foreach ($name in $saved.Keys) { [System.IO.File]::WriteAllBytes((Join-Path $state $name), $saved[$name]) }
    Write-Host 'Manifest changed: package reinstalled, LocalState restored.' -ForegroundColor Yellow
}
Write-Host "Done. Open Game Bar (Win+G) -> Widgets -> Allycord." -ForegroundColor Green
