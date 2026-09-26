param([ValidateSet('','KIOSK','IMBISS','RESTAURANT')][string]$Edition = "")
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$updates=if($env:TOR_CLOUD_UPDATES){$env:TOR_CLOUD_UPDATES}else{Join-Path $root 'updates'}
& node (Join-Path $root 'update-store.js') disable $updates $Edition
if($LASTEXITCODE -ne 0){throw 'Update konnte nicht deaktiviert werden.'}
Write-Host "TOR Update deaktiviert.$(if($Edition){' ('+$Edition+')'})"
