<#
 Geliştirici/hata ayıklama: servisi elle, görünür konsolda çalıştırır.
 Eklenti CAD_AI_TOKEN ortam değişkenini görürse kendi servisini başlatmaz, bunu kullanır.
 Bu yüzden AutoCAD'i AYNI PowerShell oturumundan başlatın (-LaunchAutoCAD).
#>
param([string]$AutoCADExe = 'C:\Program Files\Autodesk\AutoCAD 2022\acad.exe', [switch]$LaunchAutoCAD, [int]$Port = 8765)
$root = Split-Path -Parent $PSScriptRoot
$bytes = New-Object byte[] 24
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$env:CAD_AI_TOKEN = ([BitConverter]::ToString($bytes)).Replace('-', '')
$env:CAD_AI_PORT = "$Port"
if (-not $env:OPENAI_API_KEY) { Write-Warning 'OPENAI_API_KEY ayarlı değil: $env:OPENAI_API_KEY = "sk-..."' }
if ($LaunchAutoCAD) { Start-Process -FilePath $AutoCADExe }
python (Join-Path $root 'service\server.py')
