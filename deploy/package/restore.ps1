<#
  Khôi phục CRM từ thư mục sao lưu (CRM_DB.bak + files.tgz). GHI ĐÈ dữ liệu hiện tại.
  Chạy:  powershell -ExecutionPolicy Bypass -File .\restore.ps1 .\backups\20261113-153000
  Cũng dùng được để chuyển dữ liệu từ máy khác (đặt CRM_DB.bak + files.tgz vào một thư mục).
#>
param(
    [Parameter(Mandatory = $true)][string]$BackupDir,
    [switch]$Force          # bỏ qua câu hỏi xác nhận (dùng khi chạy tự động)
)
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
function Fail($m) { Write-Host "[LỖI] $m" -ForegroundColor Red; exit 1 }
if (-not (Test-Path .\.env)) { Fail "Chưa cài đặt (không có .env). Chạy install.ps1 trước." }
$bak = Join-Path $BackupDir "CRM_DB.bak"; $files = Join-Path $BackupDir "files.tgz"
if (-not (Test-Path $bak)) { Fail "Không thấy $bak" }
$cfg = @{}; Get-Content .\.env | Where-Object { $_ -match '^\s*([A-Z_]+)=(.*)$' } | ForEach-Object { $cfg[$Matches[1]] = $Matches[2].Trim() }

if (-not $Force) {
    $answer = Read-Host "Khôi phục sẽ GHI ĐÈ toàn bộ dữ liệu CRM hiện tại. Gõ YES để tiếp tục"
    if ($answer -ne "YES") { Write-Host "Đã hủy."; exit 0 }
}

Write-Host "[CRM] Dừng web..." -ForegroundColor Cyan
& docker compose stop web

Write-Host "[CRM] Khôi phục database..." -ForegroundColor Cyan
& docker compose exec -T db mkdir -p /var/opt/mssql/backup
& docker compose cp $bak db:/var/opt/mssql/backup/restore.bak
& docker compose exec -T -u root db chown mssql /var/opt/mssql/backup/restore.bak
# Tên file vật lý trong container SQL Server Linux là mặc định nên không cần MOVE
& docker compose exec -T db /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $cfg['CRM_SA_PASSWORD'] -b `
    -Q "IF DB_ID('CRM_DB') IS NOT NULL ALTER DATABASE CRM_DB SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE CRM_DB FROM DISK='/var/opt/mssql/backup/restore.bak' WITH REPLACE; ALTER DATABASE CRM_DB SET MULTI_USER;"
if ($LASTEXITCODE -ne 0) { & docker compose start web; Fail "Khôi phục database thất bại (xem thông báo phía trên)." }

Write-Host "[CRM] Khởi động web..." -ForegroundColor Cyan
& docker compose start web
if (Test-Path $files) {
    Write-Host "[CRM] Khôi phục file đính kèm..." -ForegroundColor Cyan
    & docker compose cp $files web:/tmp/files.tgz
    & docker compose exec -T -u root web sh -c "rm -rf /app/uploads/* && tar xzf /tmp/files.tgz -C /app/uploads && rm -f /tmp/files.tgz"
}
Write-Host "[OK]  Đã khôi phục từ $BackupDir" -ForegroundColor Green
