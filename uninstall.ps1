param([switch]$RemoveData)   # -RemoveData: ayarları, şifreli anahtarı, günlükleri ve yedekleri de siler
$ErrorActionPreference = 'Stop'
if (Get-Process acad -ErrorAction SilentlyContinue) { throw 'AutoCAD açık. Önce kapatın.' }
$bundle = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\CadAiDesigner.bundle'
if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force; Write-Host "Silindi: $bundle" } else { Write-Host 'Paket zaten kurulu değil.' }
if ($RemoveData) {
  $d = Join-Path $env:LOCALAPPDATA 'CadAiDesigner'
  if (Test-Path $d) { Remove-Item $d -Recurse -Force; Write-Host "Silindi: $d" }
}
