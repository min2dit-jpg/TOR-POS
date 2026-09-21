param([ValidateSet("STABLE","PILOT")][string]$Channel = "STABLE")
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$updates=if($env:TOR_CLOUD_UPDATES){$env:TOR_CLOUD_UPDATES}else{Join-Path $root 'updates'}
$action=if($Channel -eq 'PILOT'){'disable-pilot'}else{'disable'}
& node (Join-Path $root 'update-store.js') $action $updates
if($LASTEXITCODE -ne 0){throw 'Update konnte nicht deaktiviert werden.'}
Write-Host "TOR Update deaktiviert: $Channel"
