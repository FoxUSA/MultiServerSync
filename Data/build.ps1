<#
    Сборка MultiServerSync.

    Скрипт лежит внутри Data — там же, куда кладётся собранная программа.

    Результат:
      <корень>\MultiServerSync.exe  — лаунчер, единственный файл в корне
      <корень>\Data\               — программа, библиотеки, иконки, исходники и manifest.json с версией
      <корень>\UserData\           — настройки пользователя (сборкой не трогается)

    Всем трём папкам прописываются родные иконки (desktop.ini + .ico в Data).
    Программа при каждом запуске проверяет их и восстанавливает сама.

    Версия: без параметров остаётся текущая из manifest.json — пересборка номер не меняет.
    -Bump увеличивает последнюю цифру, -Version ставит номер явно.
    Программа берёт номер только оттуда и показывает его в шапке.

    Запуск:  powershell -ExecutionPolicy Bypass -File .\Data\build.ps1
             powershell -ExecutionPolicy Bypass -File .\Data\build.ps1 -Bump
             powershell -ExecutionPolicy Bypass -File .\Data\build.ps1 -Version 2.0.0
             powershell -ExecutionPolicy Bypass -File .\Data\build.ps1 -Package

    -Package дополнительно собирает то, что выкладывается на byfox.dev:
      <над корнем>\publish\multiserversync.zip  — полная программа (UserData пустая)
      <над корнем>\publish\manifest.json        — манифест, по которому программа видит новую версию
    Оба файла кладутся на https://byfox.dev/data/multiserversync/.
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Version,

    [switch]$Bump,

    [switch]$Package
)

$ErrorActionPreference = 'Stop'

$dataDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $dataDir
$workspace = Split-Path -Parent $root
$userDataDir = Join-Path $root 'UserData'
$manifestPath = Join-Path $dataDir 'manifest.json'
$assetsDir = Join-Path $dataDir 'src\App\Assets'
$appProj = Join-Path $dataDir 'src\App\MultiServerSync.csproj'
$launcherProj = Join-Path $dataDir 'src\Launcher\Launcher.csproj'
$appOut = Join-Path $env:TEMP 'multiserversync-app'
$launcherOut = Join-Path $env:TEMP 'multiserversync-launcher'
$packageStage = Join-Path $env:TEMP 'multiserversync-package'
$publishDir = Join-Path $workspace 'publish'

# что в Data не является результатом сборки и должно пережить очистку
$keep = @('src', 'build.ps1', 'README.md')

# ── версия ───────────────────────────────────────────────────────────────────

if (-not $Version) {
    $current = '1.0.0'

    if (Test-Path $manifestPath) {
        try {
            $parsed = (Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json).version
            if ($parsed) { $current = [string]$parsed }
        }
        catch {
            Write-Host "manifest.json не читается, версия считается от $current" -ForegroundColor Yellow
        }
    }

    $parts = @($current -split '\.')
    while ($parts.Count -lt 3) { $parts += '0' }

    if ($Bump) { $parts[2] = [string]([int]$parts[2] + 1) }
    $Version = ($parts[0..2]) -join '.'
}

Write-Host "== Сборка MultiServerSync $Version ($Configuration) ==" -ForegroundColor Cyan

New-Item -ItemType Directory -Force $userDataDir | Out-Null

Write-Host "Очистка Data (кроме: $($keep -join ', '))..."
Get-ChildItem $dataDir -Force |
    Where-Object { $keep -notcontains $_.Name } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# Публиковать напрямую в Data нельзя: Data — родительский каталог для Data\src\App,
# и тогда WPF ломает второй проход markup-компиляции (BAML страниц не попадает в сборку,
# приложение молча стартует без окна). Поэтому публикуем во временную папку и копируем.
Write-Host "Публикация программы..."
if (Test-Path $appOut) { Remove-Item $appOut -Recurse -Force }
dotnet publish $appProj -c $Configuration -o $appOut --nologo -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "Сборка программы завершилась с ошибкой ($LASTEXITCODE)." }

$appDll = Join-Path $appOut 'MultiServerSync.App.dll'
if ((Get-Item $appDll).Length -lt 60kb) {
    throw "Сборка получилась подозрительно маленькой ($((Get-Item $appDll).Length) байт) — XAML не скомпилировался."
}

Write-Host "Копирование программы в Data..."
Copy-Item (Join-Path $appOut '*') $dataDir -Recurse -Force

Write-Host "Публикация лаунчера..."
if (Test-Path $launcherOut) { Remove-Item $launcherOut -Recurse -Force }
dotnet publish $launcherProj -c $Configuration -o $launcherOut --nologo -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "Сборка лаунчера завершилась с ошибкой ($LASTEXITCODE)." }

Copy-Item (Join-Path $launcherOut 'MultiServerSync.exe') $root -Force

# ── манифест ─────────────────────────────────────────────────────────────────

Write-Host "Запись manifest.json..."
$manifest = [ordered]@{
    product = 'MultiServerSync'
    version = $Version
    built   = (Get-Date -Format 'yyyy-MM-dd HH:mm')
}

$json = $manifest | ConvertTo-Json
[IO.File]::WriteAllText($manifestPath, $json, (New-Object Text.UTF8Encoding($false)))

# ── иконки папок ─────────────────────────────────────────────────────────────

function Set-FolderIcon {
    param([string]$Folder, [string]$IconRelativePath)

    $iniPath = Join-Path $Folder 'desktop.ini'
    $text = "[.ShellClassInfo]`r`nConfirmFileOp=0`r`nIconResource=$IconRelativePath,0`r`nIconFile=$IconRelativePath`r`nIconIndex=0`r`n"

    if (Test-Path $iniPath) { (Get-Item $iniPath -Force).Attributes = 'Normal' }
    [IO.File]::WriteAllText($iniPath, $text, (New-Object Text.UTF8Encoding($false)))

    (Get-Item $iniPath -Force).Attributes = 'Hidden, System, Archive'

    # без «только чтение» у папки проводник в desktop.ini не заглядывает
    $dir = Get-Item $Folder -Force
    $dir.Attributes = $dir.Attributes -bor [IO.FileAttributes]::ReadOnly
}

# .ico лежат в Data: обновление заменяет Data целиком, значит иконки идут вместе с программой
Write-Host "Иконки папок..."
foreach ($icon in 'app.ico', 'data.ico', 'user_data.ico') {
    Copy-Item (Join-Path $assetsDir $icon) (Join-Path $dataDir $icon) -Force
    (Get-Item (Join-Path $dataDir $icon)).Attributes = 'Normal'
}

Set-FolderIcon -Folder $root -IconRelativePath 'Data\app.ico'
Set-FolderIcon -Folder $dataDir -IconRelativePath 'data.ico'
Set-FolderIcon -Folder $userDataDir -IconRelativePath '..\Data\user_data.ico'

# ── архив для выкладки ───────────────────────────────────────────────────────

if ($Package) {
    Write-Host "Упаковка архива для byfox.dev..."

    if (Test-Path $packageStage) { Remove-Item $packageStage -Recurse -Force }

    $stageRoot = Join-Path $packageStage 'MultiServerSync'
    $stageData = Join-Path $stageRoot 'Data'
    New-Item -ItemType Directory -Force $stageData | Out-Null

    Copy-Item (Join-Path $dataDir '*') $stageData -Recurse -Force
    Copy-Item (Join-Path $root 'MultiServerSync.exe') $stageRoot -Force

    # сборочный мусор исходников в архив не едет
    Get-ChildItem -Recurse -Directory (Join-Path $stageData 'src') -Include bin, obj -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

    # UserData в архиве пустая: чужие серверы и пароли выкладывать нельзя,
    # да и обновление её всё равно не берёт
    $stageUserData = Join-Path $stageRoot 'UserData'
    New-Item -ItemType Directory -Force $stageUserData | Out-Null

    Set-FolderIcon -Folder $stageRoot -IconRelativePath 'Data\app.ico'
    Set-FolderIcon -Folder $stageData -IconRelativePath 'data.ico'
    Set-FolderIcon -Folder $stageUserData -IconRelativePath '..\Data\user_data.ico'

    New-Item -ItemType Directory -Force $publishDir | Out-Null
    # имя архива регистрозависимо: на byfox.dev файл лежит ровно как multiserversync.zip,
    # программа скачивает его по этому же имени (UpdateService.PackageUrl)
    $zipPath = Join-Path $publishDir 'multiserversync.zip'
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    # CreateFromDirectory в PowerShell 5.1 пишет пути с обратными слэшами — такой архив
    # проводник разбирает криво. Поэтому складываем по файлу и с прямыми слэшами.
    # ZipFile лежит в одной сборке, ZipArchiveMode — в другой, нужны обе
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)

    try {
        $base = (Get-Item $packageStage).FullName.TrimEnd('\') + '\'

        foreach ($file in Get-ChildItem $packageStage -Recurse -File -Force) {
            $entryName = $file.FullName.Substring($base.Length).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }

    Copy-Item $manifestPath (Join-Path $publishDir 'manifest.json') -Force
    Remove-Item $packageStage -Recurse -Force

    $size = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Выложить на https://byfox.dev/data/multiserversync/ :" -ForegroundColor Cyan
    Write-Host "  $zipPath ($size МБ)"
    Write-Host "  $(Join-Path $publishDir 'manifest.json')"
}

# эта папка — публиш: в UserData не должно быть ничьих настроек и паролей
$strays = @(Get-ChildItem $userDataDir -Force -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne 'desktop.ini' })

if ($strays.Count -gt 0) {
    Write-Host ""
    Write-Host "ВНИМАНИЕ: в UserData лежат пользовательские файлы ($($strays.Count) шт.)" -ForegroundColor Red
    Write-Host "  $userDataDir"
    Write-Host "  В publish\multiserversync.zip они не попали, но если архивировать папку руками," -ForegroundColor Red
    Write-Host "  на сайт уедут пароли из servers.json. Папку надо держать пустой." -ForegroundColor Red
}

Write-Host ""
Write-Host "Готово. Версия $Version" -ForegroundColor Green
Write-Host "  Запуск:    $(Join-Path $root 'MultiServerSync.exe')"
Write-Host "  Программа: $dataDir"
Write-Host "  Настройки: $userDataDir"
