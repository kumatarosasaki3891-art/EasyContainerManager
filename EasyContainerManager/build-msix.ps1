param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = "1.2.1.0",

    [ValidateSet("x64", "arm64", "x86")]
    [string[]]$Architectures = @("x64"),

    [switch]$Bundle,
    [string]$PfxPath = "",
    [string]$PfxPassword = "",
    [switch]$SignTestCert
)

$ErrorActionPreference = "Stop"

$projectDir = $PSScriptRoot
$projectFile = Join-Path $projectDir "EasyContainerManager.fsproj"
$packagingDir = Join-Path $projectDir "Packaging\msix"
$manifestTemplate = Join-Path $packagingDir "AppxManifest.xml"
$assetsSource = Join-Path $packagingDir "Assets"
$outputRoot = Join-Path $projectDir "bin\msix"
$stagingRoot = Join-Path $projectDir "obj\msix"

function Find-WindowsSdkTool([string]$toolName) {
    $kitRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (-not (Test-Path $kitRoot)) {
        throw "Windows 10/11 SDK was not found at '$kitRoot'."
    }
    $candidates = Get-ChildItem -Path $kitRoot -Filter $toolName -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending
    if (-not $candidates -or $candidates.Count -eq 0) {
        throw "Could not locate '$toolName' under '$kitRoot'."
    }
    return $candidates[0].FullName
}

$makeAppx = Find-WindowsSdkTool "makeappx.exe"
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

$assemblyVersion = $Version
$packagePaths = @()
$bundleInputDir = Join-Path $stagingRoot "bundle"
if ($Bundle -or $Architectures.Count -gt 1) {
    if (Test-Path $bundleInputDir) {
        Remove-Item -Recurse -Force $bundleInputDir
    }
    New-Item -ItemType Directory -Force -Path $bundleInputDir | Out-Null
}

foreach ($arch in $Architectures) {
    $rid = "win-$arch"
    $stageDir = Join-Path $stagingRoot $arch
    if (Test-Path $stageDir) {
        Remove-Item -Recurse -Force $stageDir
    }
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

    Write-Host "Publishing $rid ($Configuration, v$Version)..."
    & dotnet publish $projectFile `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -p:Version=$Version `
        -p:AssemblyVersion=$assemblyVersion `
        -p:FileVersion=$assemblyVersion `
        -o $stageDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $rid with exit code $LASTEXITCODE."
    }

    Get-ChildItem -Path $stageDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    $stageAssets = Join-Path $stageDir "Assets"
    New-Item -ItemType Directory -Force -Path $stageAssets | Out-Null
    Copy-Item -Path (Join-Path $assetsSource "*") -Destination $stageAssets -Force

    [xml]$manifest = Get-Content -Path $manifestTemplate -Encoding UTF8
    $manifest.Package.Identity.Version = $Version
    $manifest.Package.Identity.ProcessorArchitecture = $arch
    $manifestPath = Join-Path $stageDir "AppxManifest.xml"
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $writer = New-Object System.IO.StreamWriter($manifestPath, $false, $utf8NoBom)
    $manifest.Save($writer)
    $writer.Close()

    $msixName = "EasyContainerManager_${Version}_${arch}.msix"
    $msixOut = Join-Path $outputRoot $msixName
    Write-Host "Packing $msixOut..."
    & $makeAppx pack /d $stageDir /p $msixOut /o
    if ($LASTEXITCODE -ne 0) {
        throw "makeappx pack failed for $arch with exit code $LASTEXITCODE."
    }
    $packagePaths += $msixOut

    if ($Bundle -or $Architectures.Count -gt 1) {
        Copy-Item -Path $msixOut -Destination (Join-Path $bundleInputDir $msixName) -Force
    }
}

$finalTargets = $packagePaths
if ($Bundle -or $Architectures.Count -gt 1) {
    $bundleOut = Join-Path $outputRoot "EasyContainerManager_${Version}.msixbundle"
    Write-Host "Creating bundle $bundleOut..."
    & $makeAppx bundle /d $bundleInputDir /p $bundleOut /o
    if ($LASTEXITCODE -ne 0) {
        throw "makeappx bundle failed with exit code $LASTEXITCODE."
    }
    $finalTargets = @($bundleOut)
}

if (-not [string]::IsNullOrWhiteSpace($PfxPath) -or $SignTestCert) {
    $signTool = Find-WindowsSdkTool "signtool.exe"
    $certFile = $PfxPath
    $certPass = $PfxPassword

    if ($SignTestCert) {
        $certFile = Join-Path $outputRoot "EasyContainerManager.DevTest.pfx"
        if ([string]::IsNullOrWhiteSpace($certPass)) {
            $certPass = "EasyContainerManagerDev!"
        }
        $publisher = ([xml](Get-Content -Path $manifestTemplate -Encoding UTF8)).Package.Identity.Publisher
        $existing = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher } | Select-Object -First 1
        if (-not $existing) {
            Write-Host "Creating self-signed test certificate for $publisher..."
            $existing = New-SelfSignedCertificate `
                -Type Custom `
                -Subject $publisher `
                -KeyUsage DigitalSignature `
                -FriendlyName "EasyContainerManager Dev Test" `
                -CertStoreLocation "Cert:\CurrentUser\My" `
                -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
        }
        $securePass = ConvertTo-SecureString -String $certPass -Force -AsPlainText
        Export-PfxCertificate -Cert $existing -FilePath $certFile -Password $securePass | Out-Null
    }

    foreach ($target in $finalTargets) {
        Write-Host "Signing $target..."
        if ([string]::IsNullOrWhiteSpace($certPass)) {
            & $signTool sign /fd SHA256 /f $certFile $target
        } else {
            & $signTool sign /fd SHA256 /f $certFile /p $certPass $target
        }
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed for $target with exit code $LASTEXITCODE."
        }
    }
}

Write-Host ""
Write-Host "MSIX build complete:"
foreach ($item in $finalTargets) {
    Write-Host "  $item"
}
if ([string]::IsNullOrWhiteSpace($PfxPath) -and -not $SignTestCert) {
    Write-Host "Note: Package is unsigned (ready for Microsoft Store Partner Center upload)."
    Write-Host "      To sideload locally for testing, pass -SignTestCert."
}
