param(
    [switch]$SkipSigning
)

$ErrorActionPreference = "Stop"

if (!$SkipSigning -and $PSVersionTable.PSEdition -eq "Core") {
    $windowsDir = if ($env:WINDIR) { $env:WINDIR } elseif ($env:SystemRoot) { $env:SystemRoot } else { "C:\Windows" }
    $windowsPowerShell = Join-Path $windowsDir "System32\WindowsPowerShell\v1.0\powershell.exe"
    if (Test-Path -LiteralPath $windowsPowerShell) {
        Write-Host "MSIX signing requires Windows PowerShell certificate cmdlets. Relaunching with Windows PowerShell..."
        & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
        exit $LASTEXITCODE
    }
}

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$distDir = Join-Path $projectRoot "dist\msix"
$packageDir = Join-Path $distDir "package"
$assetsDir = Join-Path $packageDir "Assets"
$version = "1.0.14.0"
$identityName = "julienpiron.fr.NexusWpp"
$msixPath = Join-Path $distDir ($identityName + "_" + $version + "_x64.msix")
$publisher = "CN=C3E3A6F0-11D2-4EE1-B3F2-34EED4CAE7FA"
$publisherDisplayName = "JULIENPIRON.FR"

function Get-WindowsSdkToolPath {
    param([string]$ToolName)

    $command = Get-Command $ToolName -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
    if ($command) { return $command }

    $sdkRoots = @(
        "C:\Program Files (x86)\Windows Kits\10\bin",
        "C:\Program Files\Windows Kits\10\bin"
    )

    foreach ($root in $sdkRoots) {
        if (!(Test-Path -LiteralPath $root)) { continue }
        $candidate = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName ("x64\" + $ToolName) } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($candidate) { return $candidate }
    }

    return $null
}

$makeAppx = Get-WindowsSdkToolPath "makeappx.exe"
$makePri = Get-WindowsSdkToolPath "makepri.exe"
$signTool = Get-WindowsSdkToolPath "signtool.exe"

if (!(Test-Path -LiteralPath $makeAppx)) {
    throw "makeappx.exe not found. Install the Windows SDK or add makeappx.exe to PATH."
}
if (!(Test-Path -LiteralPath $makePri)) {
    throw "makepri.exe not found. Install the Windows SDK or add makepri.exe to PATH."
}
if (!(Test-Path -LiteralPath $signTool)) {
    throw "signtool.exe not found. Install the Windows SDK or add signtool.exe to PATH."
}

& (Join-Path $projectRoot "compile.ps1")

if (Test-Path -LiteralPath $distDir) {
    Remove-Item -LiteralPath $distDir -Recurse -Force
}
New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null

$payloadFiles = @(
    "app.js",
    "style.css",
    "index.html",
    "julienpiron.png",
    "splash.jpg",
    "icon.ico"
)

foreach ($file in $payloadFiles) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination (Join-Path $packageDir $file) -Force
}

$binFiles = @(
    "nexuswpp.exe",
    "Microsoft.Web.WebView2.Core.dll",
    "Microsoft.Web.WebView2.WinForms.dll",
    "WebView2Loader.dll"
)

foreach ($file in $binFiles) {
    Copy-Item -LiteralPath (Join-Path $projectRoot ("bin\" + $file)) -Destination (Join-Path $packageDir $file) -Force
}

Add-Type -AssemblyName System.Drawing
function New-LogoPng {
    param(
        [string]$Path,
        [int]$Width,
        [int]$Height,
        [int]$Padding = 0
    )

    $bitmap = New-Object System.Drawing.Bitmap $Width, $Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.Clear([System.Drawing.Color]::Transparent)

            $logo = [System.Drawing.Image]::FromFile((Join-Path $projectRoot "julienpiron.png"))
            try {
                $size = [Math]::Min($Width - (2 * $Padding), $Height - (2 * $Padding))
                if ($size -le 0) { throw "Invalid logo padding for ${Width}x${Height}." }
                $x = [Math]::Round(($Width - $size) / 2)
                $y = [Math]::Round(($Height - $size) / 2)
                $graphics.DrawImage($logo, $x, $y, $size, $size)
            } finally {
                $logo.Dispose()
            }
        } finally {
            $graphics.Dispose()
        }

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $bitmap.Dispose()
    }
}

function New-ScaledLogoSet {
    param(
        [string]$Name,
        [int]$BaseWidth,
        [int]$BaseHeight
    )

    New-LogoPng -Path (Join-Path $assetsDir ($Name + ".png")) -Width $BaseWidth -Height $BaseHeight

    foreach ($scale in @(100, 125, 150, 200, 400)) {
        $width = [int][Math]::Round($BaseWidth * $scale / 100)
        $height = [int][Math]::Round($BaseHeight * $scale / 100)
        New-LogoPng -Path (Join-Path $assetsDir ($Name + ".scale-" + $scale + ".png")) -Width $width -Height $height
    }
}

function New-AppIconTargetAssets {
    foreach ($size in @(16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256)) {
        New-LogoPng -Path (Join-Path $assetsDir ("Square44x44Logo.targetsize-" + $size + ".png")) -Width $size -Height $size
        New-LogoPng -Path (Join-Path $assetsDir ("Square44x44Logo.targetsize-" + $size + "_altform-unplated.png")) -Width $size -Height $size
    }
}

New-ScaledLogoSet -Name "StoreLogo" -BaseWidth 50 -BaseHeight 50
New-ScaledLogoSet -Name "Square44x44Logo" -BaseWidth 44 -BaseHeight 44
New-ScaledLogoSet -Name "Square150x150Logo" -BaseWidth 150 -BaseHeight 150
New-AppIconTargetAssets

$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
  xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap uap10 desktop rescap">
  <Identity Name="$identityName" Publisher="$publisher" Version="$version" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>NexusWpp</DisplayName>
    <PublisherDisplayName>$publisherDisplayName</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26200.0" />
  </Dependencies>
  <Resources>
    <Resource Language="fr-fr" />
  </Resources>
  <Applications>
    <Application
      Id="NexusWpp"
      Executable="nexuswpp.exe"
      EntryPoint="Windows.FullTrustApplication"
      uap10:RuntimeBehavior="packagedClassicApp"
      uap10:TrustLevel="mediumIL">
      <uap:VisualElements
        DisplayName="NexusWpp"
        Description="Fond d'ecran telemetry cockpit"
        BackgroundColor="transparent"
        Square150x150Logo="Assets\Square150x150Logo.png"
        Square44x44Logo="Assets\Square44x44Logo.png" />
      <Extensions>
        <desktop:Extension
          Category="windows.startupTask"
          Executable="nexuswpp.exe"
          EntryPoint="Windows.FullTrustApplication">
          <desktop:StartupTask
            TaskId="NexusWppStartup"
            Enabled="true"
            DisplayName="NexusWpp" />
        </desktop:Extension>
      </Extensions>
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
"@

Set-Content -LiteralPath (Join-Path $packageDir "AppxManifest.xml") -Value $manifest -Encoding UTF8

$priConfigPath = Join-Path $packageDir "priconfig.xml"
& $makePri createconfig /cf $priConfigPath /dq fr-FR /o
if ($LASTEXITCODE -ne 0) {
    throw "makepri createconfig failed with exit code $LASTEXITCODE"
}

$priPath = Join-Path $packageDir "resources.pri"
& $makePri new /pr $packageDir /cf $priConfigPath /mn (Join-Path $packageDir "AppxManifest.xml") /of $priPath /o
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath (Join-Path $packageDir "resources.pri"))) {
    throw "makepri new failed with exit code $LASTEXITCODE"
}

Remove-Item -LiteralPath $priConfigPath -Force

& $makeAppx pack /d $packageDir /p $msixPath /o
if ($LASTEXITCODE -ne 0) {
    throw "makeappx failed with exit code $LASTEXITCODE"
}

if (!$SkipSigning) {
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey } | Sort-Object NotAfter -Descending | Select-Object -First 1
    if (!$cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName "NexusWpp MSIX Test Certificate" -CertStoreLocation "Cert:\CurrentUser\My" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
    }

    & $signTool sign /fd SHA256 /sha1 $cert.Thumbprint $msixPath
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed with exit code $LASTEXITCODE"
    }
} else {
    Write-Warning "MSIX signing skipped. Use this for Store packaging validation only."
}

Get-Item -LiteralPath $msixPath | Select-Object FullName, Length, LastWriteTime
