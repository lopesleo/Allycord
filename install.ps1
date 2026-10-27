# Allycord installer / updater.
#
#   irm https://raw.githubusercontent.com/lopesleo/Allycord/main/install.ps1 | iex
#
# Downloads the newest release, checks its SHA256, trusts the package certificate (one admin prompt,
# only the first time) and installs or updates Allycord for the current user. Settings are kept.

function Install-Allycord {
    param(
        [string]$Repo = 'lopesleo/Allycord',
        # Download and verify only; install nothing (for testing this script).
        [switch]$DryRun
    )

    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest is far slower with the progress bar

    if (-not [Environment]::Is64BitOperatingSystem) { throw 'Allycord needs 64-bit Windows.' }

    Write-Host 'Allycord installer' -ForegroundColor Green
    # /releases (not /releases/latest) so betas count as the newest release.
    # Assign first: Windows PowerShell 5.1 passes a JSON array down the pipeline as one item.
    $releases = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases?per_page=10" -Headers @{ 'User-Agent' = 'Allycord-installer' }
    $release = $releases | Where-Object { -not $_.draft } | Select-Object -First 1
    $zipAsset = $release.assets | Where-Object name -like 'Allycord-*-x64.zip' | Select-Object -First 1
    $hashAsset = $release.assets | Where-Object name -eq "$($zipAsset.name).sha256" | Select-Object -First 1
    if (-not $zipAsset) { throw "No installer found in release $($release.tag_name)." }
    if (-not $hashAsset) { throw "Release $($release.tag_name) has no checksum; refusing to install." }
    Write-Host "Version $($release.tag_name)"

    $work = Join-Path ([IO.Path]::GetTempPath()) ("allycord-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $work | Out-Null
    try {
        $zip = Join-Path $work $zipAsset.name
        Write-Host 'Downloading...'
        Invoke-WebRequest $zipAsset.browser_download_url -OutFile $zip -UseBasicParsing
        $expected = ((Invoke-RestMethod $hashAsset.browser_download_url) -split '\s+')[0].Trim().ToUpperInvariant()
        $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "Checksum mismatch (expected $expected, got $actual). Download aborted." }
        Write-Host 'Checksum OK.'

        $pkg = Join-Path $work 'pkg'
        Expand-Archive $zip $pkg
        $cer = Get-ChildItem $pkg -Filter *.cer | Select-Object -First 1
        $msix = Get-ChildItem $pkg -Filter *.msix | Select-Object -First 1
        if ($DryRun) {
            Write-Host "Dry run: would install $($msix.Name)." -ForegroundColor Yellow
            return
        }

        # Sideloaded packages must be signed by a trusted certificate; Allycord's is self-signed.
        $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cer.FullName
        $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cert.Thumbprint
        if (-not $trusted) {
            Write-Host 'Trusting the Allycord certificate (Windows will ask for admin once)...'
            $import = "Import-Certificate -FilePath '$($cer.FullName)' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
            $p = Start-Process powershell -Verb RunAs -Wait -PassThru -WindowStyle Hidden `
                -ArgumentList '-NoProfile', '-Command', $import
            if ($p.ExitCode -ne 0) { throw 'The certificate was not trusted (admin prompt declined?).' }
        }

        Write-Host 'Installing...'
        foreach ($dep in Get-ChildItem (Join-Path $pkg 'Dependencies\x64') -Filter *.appx) {
            try { Add-AppxPackage -Path $dep.FullName -ErrorAction Stop } catch { } # already installed (same or newer)
        }
        Get-Process Allycord.Widget, Allycord.Bridge -ErrorAction SilentlyContinue | Stop-Process -Force
        Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown

        Write-Host ''
        Write-Host "Allycord $($release.tag_name) installed." -ForegroundColor Green
        Write-Host 'Open Game Bar (Win+G or the Xbox button) > Widgets > Allycord, and star it.'
    }
    finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($MyInvocation.InvocationName -ne '.') { Install-Allycord }
