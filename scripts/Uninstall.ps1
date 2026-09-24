$ErrorActionPreference = 'Stop'
$programsRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'))
$expectedDirectory = [IO.Path]::GetFullPath((Join-Path $programsRoot 'GridPilot'))
if (-not (Test-Path -LiteralPath $expectedDirectory)) { Write-Output 'GridPilot is not installed.'; exit 0 }
$resolvedDirectory = (Resolve-Path -LiteralPath $expectedDirectory).Path
if ($resolvedDirectory -ne $expectedDirectory -or -not $resolvedDirectory.StartsWith($programsRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected installation path.' }
if (-not (Test-Path -LiteralPath (Join-Path $resolvedDirectory '.gridpilot-install'))) { throw 'This folder is not marked as a GridPilot installation.' }
$running = Get-Process GridPilot -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $resolvedDirectory 'GridPilot.exe') }
if ($running) { throw 'Quit GridPilot from its tray menu before uninstalling.' }
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startup = (Get-ItemProperty -LiteralPath $runKey -Name GridPilot -ErrorAction SilentlyContinue).GridPilot
if ($startup -and $startup.Contains($resolvedDirectory)) { Remove-ItemProperty -LiteralPath $runKey -Name GridPilot }
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'GridPilot.lnk'
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut }
Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
Write-Output 'GridPilot was uninstalled. Your settings remain in LocalAppData\GridPilot.'
