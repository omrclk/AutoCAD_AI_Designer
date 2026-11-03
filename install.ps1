<#
 AutoCAD AI Designer V4 - derle ve kur (Windows PowerShell 5.1 uyumlu).
 Kullanım:  powershell -ExecutionPolicy Bypass -File .\install.ps1
 AutoCAD başka yerdeyse: -AutoCADDir 'D:\Autodesk\AutoCAD 2022'
#>
param(
  [string]$AutoCADDir = 'C:\Program Files\Autodesk\AutoCAD 2022',
  [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Step($t) { Write-Host ''; Write-Host "==> $t" -ForegroundColor Cyan }
function Run-Native([scriptblock]$cmd) {   # PS 5.1: native stderr must not become a terminating error
  $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  try { & $cmd 2>&1 | Out-Host; return $LASTEXITCODE } finally { $ErrorActionPreference = $prev }
}

Step 'AutoCAD 2022 kontrol ediliyor'
foreach ($dll in 'acmgd.dll','acdbmgd.dll','accoremgd.dll') {
  if (-not (Test-Path (Join-Path $AutoCADDir $dll))) { throw "$dll bulunamadı: $AutoCADDir  (-AutoCADDir parametresini verin)" }
}
if (Get-Process acad -ErrorAction SilentlyContinue) { throw 'AutoCAD açık. Kapatıp yeniden çalıştırın.' }

Step 'MSBuild aranıyor'
$msbuild = (Get-Command msbuild.exe -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (-not $msbuild) {
  $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
  if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
  }
}
if (-not $msbuild) { throw 'MSBuild yok. Visual Studio 2022 Build Tools kurun (".NET masaüstü derleme araçları" + ".NET Framework 4.8 hedefleme paketi").' }
Write-Host $msbuild

Step 'Python 3.9+ aranıyor'
$pyExe = $null; $pyPre = @()
foreach ($cand in @(@('python', ''), @('py', '-3'), @('python3', ''))) {
  if (-not (Get-Command $cand[0] -ErrorAction SilentlyContinue)) { continue }
  $pre = @(); if ($cand[1]) { $pre = @($cand[1]) }
  try {
    $v = [int]("" + (& $cand[0] @pre -c "import sys;print(sys.version_info[0]*100+sys.version_info[1])" 2>$null))
    if ($v -ge 309) { $pyExe = $cand[0]; $pyPre = $pre; break }
  } catch { }
}
if (-not $pyExe) { Write-Warning 'Python 3.9+ bulunamadı. python.org''dan kurun ("Add python.exe to PATH" işaretli); eklenti çalışmak için buna ihtiyaç duyar.' }
else { Write-Host ("Python: " + $pyExe + " " + ($pyPre -join ' ')) }

if ($pyExe -and -not $SkipTests) {
  Step 'Servis testleri çalıştırılıyor'
  Push-Location $root
  try { $code = Run-Native { & $pyExe @pyPre -m unittest discover -s tests } } finally { Pop-Location }
  if ($code -ne 0) { throw 'Python testleri başarısız.' }
}

Step 'Eklenti derleniyor'
$code = Run-Native { & $msbuild (Join-Path $root 'autocad\CadAiDesigner.csproj') /t:Rebuild /p:Configuration=Release "/p:AutoCADDir=$AutoCADDir" /v:minimal /nologo }
$built = Join-Path $root 'autocad\bin\Release\CadAiDesigner.dll'
if ($code -ne 0 -or -not (Test-Path $built)) { throw 'Derleme başarısız. Yukarıdaki hata satırlarını (error CS...) gönderin.' }

Step 'Kuruluyor (AutoCAD ApplicationPlugins paketi)'
$bundle = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\CadAiDesigner.bundle'
if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $bundle 'Contents\service') -Force | Out-Null
Copy-Item (Join-Path $root 'autocad\PackageContents.xml') $bundle
Copy-Item $built (Join-Path $bundle 'Contents')
Copy-Item (Join-Path $root 'service\*.py') (Join-Path $bundle 'Contents\service')
Get-ChildItem $bundle -Recurse -File | Unblock-File
$priceDir = Join-Path $env:APPDATA 'CadAiDesigner'
New-Item -ItemType Directory -Path $priceDir -Force | Out-Null
Copy-Item (Join-Path $root 'templates\birim_fiyat_sablon.csv') $priceDir -Force
if (-not (Test-Path (Join-Path $priceDir 'birim_fiyat.csv')) -and -not (Test-Path (Join-Path $priceDir 'birim_fiyat.xlsx'))) { Copy-Item (Join-Path $root 'templates\birim_fiyat_sablon.csv') (Join-Path $priceDir 'birim_fiyat.csv') }

Write-Host ''
Write-Host 'Kurulum tamam.' -ForegroundColor Green
Write-Host "Paket: $bundle"
Write-Host 'Sonraki adımlar:'
Write-Host '  1) AutoCAD 2022''yi açın (eklenti otomatik yüklenir).'
Write-Host '  2) AIDESIGNER yazın, panelde "Ayarlar" ile OpenAI API anahtarınızı girin.'
Write-Host '  3) Bir DWG KOPYASI açın, nesne seçin, isteğinizi yazın.'
