<#
  Sao lưu CRM: database (CRM_DB.bak) + file đính kèm (files.tgz) vào thư mục backups\yyyyMMdd-HHmmss.
  Chạy:  powershell -ExecutionPolicy Bypass -File .\backup.ps1
  Nên chạy định kỳ (Task Scheduler) và chép thư mục backups sang nơi khác.
#>
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
function Fail($m) { Write-Host "[LỖI] $m" -ForegroundColor Red; exit 1 }
if (-not (Test-Path .\.env)) { Fail "Chưa cài đặt (không có .env)." }
$cfg = @{}; Get-Content .\.env | Where-Object { $_ -match '^\s*([A-Z_]+)=(.*)$' } | ForEach-Object { $cfg[$Matches[1]] = $Matches[2].Trim() }

$dir = Join-Path $PSScriptRoot ("backups\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force $dir | Out-Null

Write-Host "[CRM] Sao lưu database..." -ForegroundColor Cyan
& docker compose exec -T db mkdir -p /var/opt/mssql/backup
# Không dùng COMPRESSION vì bản SQL Server Express không hỗ trợ
& docker compose exec -T db /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $cfg['CRM_SA_PASSWORD'] -b `
    -Q "BACKUP DATABASE CRM_DB TO DISK='/var/opt/mssql/backup/CRM_DB.bak' WITH INIT"
if ($LASTEXITCODE -ne 0) { Fail "Sao lưu database thất bại." }
& docker compose cp db:/var/opt/mssql/backup/CRM_DB.bak (Join-Path $dir "CRM_DB.bak")
if ($LASTEXITCODE -ne 0) { Fail "Không chép được file backup database." }

Write-Host "[CRM] Sao lưu file đính kèm..." -ForegroundColor Cyan
& docker compose exec -T web tar czf /tmp/files.tgz -C /app/uploads .
& docker compose cp web:/tmp/files.tgz (Join-Path $dir "files.tgz")
& docker compose exec -T web rm -f /tmp/files.tgz
if (-not (Test-Path (Join-Path $dir "files.tgz"))) { Fail "Không sao lưu được file đính kèm." }

Write-Host "[OK]  Đã sao lưu vào: $dir" -ForegroundColor Green
