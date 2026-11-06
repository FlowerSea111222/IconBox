$ErrorActionPreference = 'Stop'
$folder = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'IconBox'
$files = @('error.log','session.log','update.log','layout.json','layout.json.previous','layout.json.last-good','removed.json') | ForEach-Object { Join-Path $folder $_ } | Where-Object { Test-Path -LiteralPath $_ }
if (-not $files) { throw 'No diagnostic files found. Run Start.cmd first.' }
$destination = Join-Path ([Environment]::GetFolderPath('Desktop')) ('IconBox_Diagnostics_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.zip')
Compress-Archive -LiteralPath $files -DestinationPath $destination
Write-Host ('Saved: ' + $destination)
