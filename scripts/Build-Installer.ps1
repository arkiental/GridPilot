param([string]$Compiler = '')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Compiler) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'D:\Codex\Tools\InnoSetup\ISCC.exe'
    )
    $Compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $Compiler -or -not (Test-Path -LiteralPath $Compiler)) { throw 'Install Inno Setup 6 or supply -Compiler with the path to ISCC.exe.' }
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'artifacts\GridPilot-win-x64\GridPilot.exe'))) { throw 'Run scripts/Publish.ps1 first.' }
$version = [version](Get-Item (Join-Path $projectRoot 'artifacts\GridPilot-win-x64\GridPilot.exe')).VersionInfo.FileVersion
$releaseVersion = "$($version.Major).$($version.Minor).$($version.Build)"
$versionMS = ([uint32]$version.Major -shl 16) -bor [uint32]$version.Minor
$versionLS = ([uint32]$version.Build -shl 16) -bor [uint32]$version.Revision
& $Compiler /Q "/DReleaseVersion=$releaseVersion" "/DVersionMS=$versionMS" "/DVersionLS=$versionLS" (Join-Path $projectRoot 'installer\GridPilot.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'artifacts\installer') -Filter '*Setup.exe'
