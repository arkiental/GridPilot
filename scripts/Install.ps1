$ErrorActionPreference = 'Stop'
$sourceDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory 'GridPilot.exe'))) { throw 'Run Install.ps1 from the published GridPilot folder.' }
$installDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\GridPilot'
$installDirectory = [IO.Path]::GetFullPath($installDirectory)
if ($sourceDirectory -eq $installDirectory) { Write-Output 'GridPilot is already in its installation folder.'; exit 0 }
$running = Get-Process GridPilot -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $installDirectory 'GridPilot.exe') }
if ($running) { throw 'Quit the installed GridPilot from its tray menu before updating.' }
New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $sourceDirectory | Copy-Item -Destination $installDirectory -Recurse -Force
[IO.File]::WriteAllText((Join-Path $installDirectory '.gridpilot-install'), 'GridPilot per-user installation')
$shortcutDirectory = [Environment]::GetFolderPath('Programs')
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $shortcutDirectory 'GridPilot.lnk'))
$shortcut.TargetPath = Join-Path $installDirectory 'GridPilot.exe'
$shortcut.WorkingDirectory = $installDirectory
$shortcut.Description = 'Arrange desktop windows with a visual grid'
$shortcut.Save()
Write-Output "Installed GridPilot to $installDirectory. Open it from the Start menu."
