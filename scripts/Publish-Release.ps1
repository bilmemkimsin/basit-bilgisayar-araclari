[CmdletBinding()]
param(
    [string]$InnoSetupCompiler,
    [switch]$PortableOnly,
    [ValidatePattern('^[a-z0-9][a-z0-9-]*$')]
    [string]$GeneratedRootName = 'phase4',
    [string]$DotnetExe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$generatedRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot (Join-Path '.tools' $GeneratedRootName)))
$appProject = Join-Path $projectRoot 'src/BasitBilgisayarAraclari.App/BasitBilgisayarAraclari.App.csproj'
$localDotnetExe = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if ([string]::IsNullOrWhiteSpace($DotnetExe)) {
    if (Test-Path -LiteralPath $localDotnetExe -PathType Leaf) {
        $DotnetExe = $localDotnetExe
    } else {
        $dotnetCommand = Get-Command 'dotnet' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $dotnetCommand) {
            $DotnetExe = $dotnetCommand.Source
        }
    }
}
$dotnetExe = $DotnetExe
$setupScript = Join-Path $projectRoot 'packaging/BasitBilgisayarAraclari.iss'
$portableReadme = Join-Path $projectRoot 'packaging/README-Portable.txt'

if (-not (Test-Path -LiteralPath $dotnetExe -PathType Leaf)) {
    throw "dotnet SDK bulunamadı. .NET 10 SDK'yı PATH'e ekleyin veya -DotnetExe ile dotnet.exe yolunu verin."
}
if (-not (Test-Path -LiteralPath $appProject -PathType Leaf)) {
    throw "Uygulama projesi bulunamadı: $appProject"
}

$versionOutput = & $dotnetExe msbuild $appProject -nologo -getProperty:Version
if ($LASTEXITCODE -ne 0) {
    throw "Uygulama sürümü MSBuild üzerinden okunamadı (exit $LASTEXITCODE)."
}
$appVersion = ($versionOutput | Out-String).Trim()
if ([string]::IsNullOrWhiteSpace($appVersion) -or $appVersion -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Geçersiz veya boş uygulama sürümü: '$appVersion'"
}

$packageRoot = Join-Path $generatedRoot 'packages'
$publishDir = Join-Path $packageRoot 'portable'
$binDir = Join-Path $generatedRoot 'bin/app'
$zipPath = Join-Path $packageRoot "BasitBilgisayarAraclari-$appVersion-win-x64-portable.zip"
$installerPath = Join-Path $packageRoot "BasitBilgisayarAraclari-$appVersion-win-x64-setup.exe"
$hashPath = Join-Path $packageRoot 'SHA256SUMS.txt'

function Reset-GeneratedDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $allowedPrefix = $generatedRoot.TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Güvenli olmayan çıktı yolu, üretim alanının dışında: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Reset-GeneratedDirectory -Path $publishDir
Reset-GeneratedDirectory -Path $binDir
foreach ($file in @($zipPath, $installerPath, $hashPath)) {
    $fullFile = [IO.Path]::GetFullPath($file)
    if (-not $fullFile.StartsWith(($packageRoot.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Güvenli olmayan çıktı dosyası: $fullFile"
    }
    if (Test-Path -LiteralPath $fullFile) {
        Remove-Item -LiteralPath $fullFile -Force
    }
}

$publishArguments = @(
    'publish', $appProject,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $publishDir,
    "-p:BaseOutputPath=$binDir/",
    '-p:DefaultItemExcludesInProjectFolder=**/.tools/obj-isolated/**'
)
& $dotnetExe @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "Self-contained win-x64 publish başarısız oldu (exit $LASTEXITCODE)."
}

$appExe = Join-Path $publishDir 'BasitBilgisayarAraclari.App.exe'
$runtimeConfigPath = Join-Path $publishDir 'BasitBilgisayarAraclari.App.runtimeconfig.json'
if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) {
    throw "Publish çıktısında uygulama başlatıcısı yok: $appExe"
}
if (-not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf)) {
    throw "Publish çıktısında runtimeconfig yok: $runtimeConfigPath"
}
$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
$frameworkNames = @($runtimeConfig.runtimeOptions.includedFrameworks | ForEach-Object { $_.name })
if ($frameworkNames -notcontains 'Microsoft.NETCore.App' -or $frameworkNames -notcontains 'Microsoft.WindowsDesktop.App') {
    throw "Publish çıktısı hem .NET hem Windows Desktop runtime'ını içermiyor."
}
$portableReadmeContent = (Get-Content -LiteralPath $portableReadme -Raw).Replace('@APP_VERSION@', $appVersion)
if ($portableReadmeContent -match '@APP_VERSION@') {
    throw 'Portable kullanım metninde sürüm yer tutucusu çözülemedi.'
}
Set-Content -LiteralPath (Join-Path $publishDir 'KULLANIM.txt') -Value $portableReadmeContent -Encoding UTF8

Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$packageFiles = @($zipPath)
if (-not $PortableOnly) {
    if ([string]::IsNullOrWhiteSpace($InnoSetupCompiler)) {
        $compilerCandidates = @(
            (Join-Path $projectRoot '.tools/innosetup-6.7.3/ISCC.exe'),
            'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
            'C:\Program Files\Inno Setup 6\ISCC.exe'
        )
        $InnoSetupCompiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
    if ([string]::IsNullOrWhiteSpace($InnoSetupCompiler) -or -not (Test-Path -LiteralPath $InnoSetupCompiler -PathType Leaf)) {
        throw "Portable paket üretildi; Inno Setup 6 derleyicisi bulunamadı. -InnoSetupCompiler ile ISCC.exe yolu verin."
    }

    $environmentValues = @{
        BBA_APP_VERSION = $env:BBA_APP_VERSION
        BBA_RELEASE_PUBLISH_DIR = $env:BBA_RELEASE_PUBLISH_DIR
        BBA_RELEASE_OUTPUT_DIR = $env:BBA_RELEASE_OUTPUT_DIR
    }
    try {
        $env:BBA_APP_VERSION = $appVersion
        $env:BBA_RELEASE_PUBLISH_DIR = $publishDir
        $env:BBA_RELEASE_OUTPUT_DIR = $packageRoot
        & $InnoSetupCompiler $setupScript
        if ($LASTEXITCODE -ne 0) {
            throw "Inno Setup derlemesi başarısız oldu (exit $LASTEXITCODE)."
        }
    } finally {
        foreach ($name in $environmentValues.Keys) {
            if ($null -eq $environmentValues[$name]) {
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            } else {
                Set-Item -LiteralPath "Env:$name" -Value $environmentValues[$name]
            }
        }
    }

    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw "Inno Setup derlemesi beklenen çıktıyı üretmedi: $installerPath"
    }
    $packageFiles += $installerPath
}
$hashes = $packageFiles | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 }
$hashLines = $hashes | ForEach-Object { '{0}  {1}' -f $_.Hash.ToLowerInvariant(), (Split-Path -Leaf $_.Path) }
Set-Content -LiteralPath $hashPath -Value $hashLines -Encoding ASCII

Write-Output "Uygulama sürümü: $appVersion"
Write-Output "Portable klasör: $publishDir"
Write-Output "Portable ZIP: $zipPath"
if ($PortableOnly) {
    Write-Output 'Kurulum paketi: üretilmedi (-PortableOnly seçildi)'
} else {
    Write-Output "Kurulum paketi: $installerPath"
}
Write-Output "SHA-256: $hashPath"
