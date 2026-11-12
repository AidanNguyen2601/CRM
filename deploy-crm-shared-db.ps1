<#
  Trien khai CRM theo PHUONG AN B: dung chung SQL Server cua app Healthcare.
  Chay tai thu muc CRM:   powershell -ExecutionPolicy Bypass -File .\deploy-crm-shared-db.ps1
  Script KHONG khoi dong lai / sua database cua Healthcare. Chi them login 'crm_app'
  (quyen dbcreator) de CRM tu tao database rieng CRM_DB.
#>
param(
    [string]$SqlContainer = "",     # bo trong de script tu tim
    [int]$WebPort = 8100
)
$ErrorActionPreference = "Continue"   # PS 5.1: tranh loi khi chuyen huong stderr cua lenh docker
Set-Location -Path $PSScriptRoot
function Info($m) { Write-Host "[CRM] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "[OK]  $m" -ForegroundColor Green }
function Fail($m) { Write-Host "[LỖI] $m" -ForegroundColor Red; exit 1 }

# 1. Docker
Info "Kiểm tra Docker..."
docker version --format '{{.Server.Version}}' *> $null
if ($LASTEXITCODE -ne 0) { Fail "Docker chưa chạy. Mở Docker Desktop rồi chạy lại script." }
Ok "Docker đang chạy"

# 2. Tim SQL Server cua Healthcare
if (-not $SqlContainer) {
    $candidates = docker ps --format '{{.Names}}|{{.Image}}' | Where-Object { $_ -match 'mssql|sqlserver|azure-sql-edge' } |
        ForEach-Object { $_.Split('|')[0] } | Where-Object { $_ -notin @('hrm-sqlserver', 'crm-sqlserver') }
    $candidates = @($candidates)
    if ($candidates.Count -eq 0) { Fail "Không tìm thấy container SQL Server nào đang chạy. Healthcare có dùng SQL Server trong Docker không? (PA B chỉ áp dụng cho SQL Server)" }
    if ($candidates.Count -gt 1) {
        Write-Host "Tìm thấy nhiều container SQL Server:"
        for ($i = 0; $i -lt $candidates.Count; $i++) { Write-Host "  [$i] $($candidates[$i])" }
        $idx = Read-Host "Chọn số thứ tự container SQL của Healthcare"
        $SqlContainer = $candidates[[int]$idx]
    } else { $SqlContainer = $candidates[0] }
}
Ok "SQL Server của Healthcare: $SqlContainer"

# 3. Network
$networks = @((docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}' $SqlContainer).Trim().Split(' ') | Where-Object { $_ })
$network = $networks | Where-Object { $_ -notin @('bridge', 'host', 'none') } | Select-Object -First 1
if (-not $network) {
    $network = "hrm-shared"
    Info "SQL Server đang ở network mặc định 'bridge' -> tạo network '$network' và nối thêm SQL Server vào (không khởi động lại container)."
    docker network inspect $network *> $null
    if ($LASTEXITCODE -ne 0) { docker network create $network | Out-Null }
    docker network connect $network $SqlContainer 2>$null
}
Ok "Network dùng chung: $network"

# 4. Mat khau SA cua Healthcare (doc tu bien moi truong container)
$envs = docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' $SqlContainer
$saLine = $envs | Where-Object { $_ -match '^(MSSQL_SA_PASSWORD|SA_PASSWORD)=' } | Select-Object -First 1
if ($saLine) { $saPwd = $saLine.Substring($saLine.IndexOf('=') + 1); Ok "Đã đọc mật khẩu SA từ cấu hình container" }
else {
    $sec = Read-Host "Nhập mật khẩu SA của SQL Server Healthcare" -AsSecureString
    $saPwd = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
}

# 5. sqlcmd trong container
docker exec $SqlContainer sh -c "test -x /opt/mssql-tools18/bin/sqlcmd" *> $null
if ($LASTEXITCODE -eq 0) { $sqlcmd = "/opt/mssql-tools18/bin/sqlcmd"; $extra = @("-C") }
else {
    docker exec $SqlContainer sh -c "test -x /opt/mssql-tools/bin/sqlcmd" *> $null
    if ($LASTEXITCODE -ne 0) { Fail "Không tìm thấy sqlcmd trong container $SqlContainer. Tạo login 'crm_app' thủ công bằng SSMS (xem README)." }
    $sqlcmd = "/opt/mssql-tools/bin/sqlcmd"; $extra = @()
}

# 6. Tao / cap nhat login crm_app
$chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789'
$rand = -join (1..12 | ForEach-Object { $chars[(Get-Random -Maximum $chars.Length)] })
$crmPwd = "Crm@$rand" + "9x"
$q = "IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = 'crm_app') CREATE LOGIN crm_app WITH PASSWORD = '$crmPwd', CHECK_POLICY = ON " +
     "ELSE ALTER LOGIN crm_app WITH PASSWORD = '$crmPwd'; ALTER SERVER ROLE dbcreator ADD MEMBER crm_app; " +
     "IF DB_ID('CRM_DB') IS NOT NULL EXEC('USE CRM_DB; IF USER_ID(''crm_app'') IS NULL CREATE USER crm_app FOR LOGIN crm_app; ALTER ROLE db_owner ADD MEMBER crm_app;');"
docker exec $SqlContainer $sqlcmd -S localhost -U sa -P $saPwd @extra -b -Q $q
if ($LASTEXITCODE -ne 0) { Fail "Không tạo được login crm_app (sai mật khẩu SA?)." }
Ok "Login 'crm_app' sẵn sàng (chỉ có quyền tạo database, không truy cập DB của Healthcare)"

# 7. Port web con trong
function PortFree($p) {
    try { $l = [System.Net.Sockets.TcpListener]::new([Net.IPAddress]::Any, $p); $l.Start(); $l.Stop(); return $true } catch { return $false }
}
$running = docker ps --filter "name=^crm-web$" -q
if (-not $running) { while (-not (PortFree $WebPort)) { $WebPort++ } }
Ok "Port web CRM: $WebPort"

# 8. Ghi .env (UTF-8 khong BOM)
$envText = @"
CRM_WEB_PORT=$WebPort
CRM_DB_PORT=1435
CRM_SA_PASSWORD=NotUsedInPlanB@1
SHARED_NETWORK=$network
SHARED_DB_HOST=$SqlContainer
SHARED_DB_USER=crm_app
SHARED_DB_PASSWORD=$crmPwd
"@
[IO.File]::WriteAllText((Join-Path $PSScriptRoot ".env"), $envText.Replace("`r`n", "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
Ok "Đã ghi file .env"

# 9. Build & chay
Info "Build và khởi động crm-web (lần đầu mất vài phút)..."
docker compose -f docker-compose.shared-db.yml up -d --build
if ($LASTEXITCODE -ne 0) { Fail "docker compose lỗi. Xem thông báo phía trên." }

# 10. Cho web san sang
Info "Đợi ứng dụng khởi tạo database và dữ liệu mẫu..."
$url = "http://localhost:$WebPort/Account/Login"
$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 5
    try { $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5; if ($r.StatusCode -eq 200) { $ready = $true; break } } catch { }
}
if (-not $ready) {
    docker compose -f docker-compose.shared-db.yml logs --tail 40 crm-web
    Fail "Ứng dụng chưa phản hồi sau 5 phút. Xem log phía trên."
}
Ok "CRM đã chạy: http://localhost:$WebPort  (admin / Admin@123)"
docker ps --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"
Start-Process "http://localhost:$WebPort"
