<#
  Đóng gói CRM thành bộ cài KHÔNG kèm mã nguồn: image Docker đã build (code đã biên dịch) + SQL Server + script cài đặt.
  Chạy trên máy phát triển (cần Docker):
      powershell -ExecutionPolicy Bypass -File .\deploy\build-package.ps1
  Tuỳ chọn:
      -Version 1.2.0     đặt số phiên bản (mặc định theo ngày giờ)
      -SkipSqlImage      không kèm image SQL Server (gói nhẹ ~150 MB, máy đích phải có Internet để tự tải)
      -NoZip             chỉ tạo thư mục, không nén zip
  Kết quả: dist\crm-<version>\ và dist\crm-<version>.zip
#>
param(
    [string]$Version = (Get-Date -Format "yyyy.MM.dd-HHmm"),
    [switch]$SkipSqlImage,
    [switch]$NoZip
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$sqlImage = "mcr.microsoft.com/mssql/server:2022-latest"
function Info($m) { Write-Host "[GÓI] $m" -ForegroundColor Cyan }
function Fail($m) { Write-Host "[LỖI] $m" -ForegroundColor Red; exit 1 }
if ($Version -notmatch '^[A-Za-z0-9_.\-]+$') { Fail "Phiên bản chỉ gồm chữ, số, dấu chấm, gạch ngang." }

# 1. Build image ứng dụng (Dockerfile nhiều tầng: chỉ bản publish đi vào image, không có mã nguồn)
Info "Build image crm-web:$Version ..."
& docker build -t "crm-web:$Version" $root
if ($LASTEXITCODE -ne 0) { Fail "Build image thất bại." }

if (-not $SkipSqlImage) {
    Info "Lấy image SQL Server ..."
    & docker pull $sqlImage
    if ($LASTEXITCODE -ne 0) { Fail "Không tải được $sqlImage (cần Internet trên máy đóng gói)." }
}

# 2. Thư mục gói
$dist = Join-Path $root "dist"
$out = Join-Path $dist "crm-$Version"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force (Join-Path $out "images") | Out-Null
Copy-Item (Join-Path $PSScriptRoot "package\*") $out -Recurse
[IO.File]::WriteAllText((Join-Path $out "VERSION"), "$Version`n", (New-Object Text.UTF8Encoding($false)))

# Script PowerShell: UTF-8 có BOM (PowerShell 5.1 mới đọc đúng tiếng Việt); script Linux: UTF-8 + xuống dòng LF
Get-ChildItem $out -Filter *.ps1 | ForEach-Object {
    [IO.File]::WriteAllText($_.FullName, [IO.File]::ReadAllText($_.FullName), (New-Object Text.UTF8Encoding($true)))
}
Get-ChildItem $out -Include *.sh, *.yml -Recurse | ForEach-Object {
    [IO.File]::WriteAllText($_.FullName, ([IO.File]::ReadAllText($_.FullName) -replace "`r`n", "`n"), (New-Object Text.UTF8Encoding($false)))
}

# 3. Xuất image ra file .tar
Info "Xuất image crm-web ..."
& docker save "crm-web:$Version" -o (Join-Path $out "images\crm-web.tar")
if ($LASTEXITCODE -ne 0) { Fail "Không xuất được image crm-web." }
if (-not $SkipSqlImage) {
    Info "Xuất image SQL Server (khoảng 1–2 GB, mất vài phút) ..."
    & docker save $sqlImage -o (Join-Path $out "images\sqlserver.tar")
    if ($LASTEXITCODE -ne 0) { Fail "Không xuất được image SQL Server." }
}

# 4. Nén zip (tar.exe có sẵn trên Windows 10+, hỗ trợ file lớn)
if (-not $NoZip) {
    Info "Nén zip ..."
    $zip = Join-Path $dist "crm-$Version.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    & tar.exe -a -c -f $zip -C $dist "crm-$Version"
    if ($LASTEXITCODE -ne 0) { Fail "Nén zip thất bại (thư mục gói vẫn dùng được)." }
}

$size = "{0:N0} MB" -f ((Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB)
Write-Host ""
Write-Host "[OK]  Đã tạo gói CRM $Version ($size)" -ForegroundColor Green
Write-Host "      Thư mục: $out"
if (-not $NoZip) { Write-Host "      File zip: $zip" }
Write-Host "      Trên máy đích: giải nén -> chạy install.ps1 (Windows) hoặc install.sh (Linux). Xem HUONG-DAN.txt"
