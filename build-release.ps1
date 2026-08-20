$ErrorActionPreference = "Stop"

$version = "1.1.1"
$projectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$solutionPath = Join-Path $projectRoot "SpeechToText.sln"
$msbuildPath = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"

if (-not (Test-Path -LiteralPath $msbuildPath)) {
    $vswherePath = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswherePath)) {
        throw "MSBuild from Visual Studio 2022 was not found."
    }

    $installationPath = & $vswherePath -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    $msbuildPath = Join-Path $installationPath "MSBuild\Current\Bin\MSBuild.exe"
}

& $msbuildPath $solutionPath /t:Restore,Build /p:Configuration=Release /p:Platform=x64 /m /v:minimal
if ($LASTEXITCODE -ne 0) {
    throw "Build failed."
}

$testsPath = Join-Path $projectRoot "tests\SpeechToText.Tests\bin\Release\SpeechToText.Tests.exe"
& $testsPath
if ($LASTEXITCODE -ne 0) {
    throw "Automated tests failed."
}

$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "artifacts"))
$stagePath = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "pis.etc-win10-x64-v$version"))
if (-not $stagePath.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe release directory path."
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
if (Test-Path -LiteralPath $stagePath) {
    Remove-Item -LiteralPath $stagePath -Recurse -Force
}
New-Item -ItemType Directory -Path $stagePath | Out-Null

$releasePath = Join-Path $projectRoot "src\SpeechToText.App\bin\x64\Release"
Copy-Item -LiteralPath (Join-Path $releasePath "SpeechToText.exe") -Destination $stagePath
Get-ChildItem -LiteralPath $releasePath -Filter "*.dll" |
    Copy-Item -Destination $stagePath
Copy-Item -LiteralPath (Join-Path $projectRoot "README.md") -Destination $stagePath
Copy-Item -LiteralPath (Join-Path $projectRoot "LICENSE") -Destination $stagePath
Copy-Item -LiteralPath (Join-Path $projectRoot "THIRD-PARTY-NOTICES.md") -Destination $stagePath

$stageDocsPath = Join-Path $stagePath "docs"
New-Item -ItemType Directory -Path $stageDocsPath | Out-Null
@(
    "ARCHITECTURE.md",
    "BUILDING.md",
    "FIRST_START.md",
    "PRIVACY.md",
    "TEST_CHECKLIST.md"
) | ForEach-Object {
    Copy-Item -LiteralPath (Join-Path $projectRoot "docs\$_") -Destination $stageDocsPath
}

$zipPath = Join-Path $artifactsRoot "pis.etc-win10-x64-v$version.zip"
Compress-Archive -Path (Join-Path $stagePath "*") -DestinationPath $zipPath -Force

Write-Host "Ready: $zipPath"
