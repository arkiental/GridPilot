param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
if ($Runtime -ne 'win-x64') { throw 'This release configuration supports win-x64.' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = Join-Path $projectRoot 'artifacts'
$publishDirectory = Join-Path $releaseRoot "GridPilot-$Runtime"
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
dotnet publish (Join-Path $projectRoot 'src\GridPilot\GridPilot.csproj') -c Release -r $Runtime --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishDirectory -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.ps1') -Destination $publishDirectory -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination $publishDirectory -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\VALIDATION.md') -Destination $publishDirectory -Force
$archive = Join-Path $releaseRoot "GridPilot-$Runtime.zip"
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archive -Force
Get-Item -LiteralPath (Join-Path $publishDirectory 'GridPilot.exe'),$archive | Select-Object FullName,Length
