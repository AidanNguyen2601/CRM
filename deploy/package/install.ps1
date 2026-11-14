<#
  Cài đặt CRM từ gói (không cần mã nguồn, không cần Internet nếu gói có kèm image SQL Server).
  Yêu cầu: Docker Desktop (Windows) đang chạy.
  Chạy:   powershell -ExecutionPolicy Bypass -File .\install.ps1 [-Port 8100] [-SampleCustomers 5]
  Chạy lại nhiều lần an toàn: lần sau chỉ nạp image mới (nâng cấp) và khởi động lại, KHÔNG xóa dữ liệu.
#>
param(
    [int]$Port = 0,
    [int]$SampleCustomers = -1,
    # Phiên bản SQL Server – chỉ áp dụng khi cài lần đầu: Developer (thử nghiệm) | Express (miễn phí, dùng thật)
    [ValidateSet("Developer", "Express")][string]$SqlEdition = "Developer",
    [switch]$NoBrowser      # không tự mở trình duyệt (cài tự động / qua script)
)
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
function Info($m) { Write-Host "[CRM] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "[OK]  $m" -ForegroundColor Green }
function Fail($m) { Write-Host "[LỖI] $m" -ForegroundColor Red; exit 1 }

# 1. Docker
Info "Kiểm tra Docker..."
& docker version --format '{{.Server.Version}}' *> $null
if ($LASTEXITCODE -ne 0) { Fail "Docker chưa chạy. Cài / mở Docker Desktop rồi chạy lại." }
& docker compose version *> $null
if ($LASTEXITCODE -ne 0) { Fail "Không có 'docker compose'. Cập nhật Docker Desktop." }
Ok "Docker sẵn sàng"

# 2. File cấu hình .env (chỉ tạo lần đầu; lần sau giữ nguyên, chỉ cập nhật phiên bản)
$version = (Get-Content .\VERSION -Raw).Trim()
function PortFree($p) { try { $l = [System.Net.Sockets.TcpListener]::new([Net.IPAddress]::Any, $p); $l.Start(); $l.Stop(); $true } catch { $false } }
if (-not (Test-Path .\.env)) {
    if ($Port -le 0) { $Port = 8100; while (-not (PortFree $Port)) { $Port++ } }
    if ($SampleCustomers -lt 0) { $SampleCustomers = 5 }
    # Mật khẩu SA ngẫu nhiên đủ độ phức tạp (hoa, thường, số, ký tự đặc biệt)
    $chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789'
    $rand = -join (1..16 | ForEach-Object { $chars[(Get-Random -Maximum $chars.Length)] })
    $env = @"
# Cấu hình CRM – giữ bí mật file này
CRM_VERSION=$version
CRM_WEB_PORT=$Port
CRM_SA_PASSWORD=Crm#${rand}9a
# Số khách hàng mẫu khi khởi tạo database lần đầu (0 hoặc để trống = bộ mẫu mặc định 8 khách hàng)
CRM_SAMPLE_CUSTOMERS=$SampleCustomers
# Phiên bản SQL Server trong container: Developer (thử nghiệm) | Express (miễn phí, dùng thật, giới hạn 10GB)
CRM_SQL_EDITION=$SqlEdition
"@
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot ".env"), $env.Replace("`r`n", "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
    Ok "Đã tạo .env (cổng $Port, $SampleCustomers khách hàng mẫu)"
} else {
    $text = Get-Content .\.env -Raw
    $text = [regex]::Replace($text, '(?m)^CRM_VERSION=.*$', "CRM_VERSION=$version")
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot ".env"), $text, (New-Object Text.UTF8Encoding($false)))
    Ok "Giữ cấu hình .env hiện có, cập nhật phiên bản $version"
}
$cfg = @{}; Get-Content .\.env | Where-Object { $_ -match '^\s*([A-Z_]+)=(.*)$' } | ForEach-Object { $cfg[$Matches[1]] = $Matches[2].Trim() }
$Port = [int]$cfg['CRM_WEB_PORT']

# 3. Nạp image
Get-ChildItem .\images -Filter *.tar -ErrorAction SilentlyContinue | ForEach-Object {
    Info "Nạp image $($_.Name) (có thể mất vài phút)..."
    & docker load -i $_.FullName
    if ($LASTEXITCODE -ne 0) { Fail "Không nạp được $($_.Name)" }
}
Ok "Đã nạp image"

# 4. Khởi động
Info "Khởi động CRM..."
& docker compose up -d
if ($LASTEXITCODE -ne 0) { Fail "docker compose lỗi – xem thông báo phía trên." }

# 5. Đợi web sẵn sàng (lần đầu SQL Server + tạo database mất 1–3 phút)
Info "Đợi ứng dụng khởi tạo..."
$url = "http://localhost:$Port"
$ready = $false
for ($i = 0; $i -lt 72; $i++) {
    Start-Sleep -Seconds 5
    try { $r = Invoke-WebRequest -Uri "$url/Account/Login" -UseBasicParsing -TimeoutSec 5; if ($r.StatusCode -eq 200) { $ready = $true; break } } catch { }
}
if (-not $ready) { & docker compose logs --tail 40 web; Fail "Ứng dụng chưa phản hồi sau 6 phút. Xem log phía trên." }

Ok "CRM đã chạy: $url"
Write-Host ""
Write-Host "  Đăng nhập lần đầu: admin / Admin@123  (hãy ĐỔI MẬT KHẨU ngay)" -ForegroundColor Yellow
Write-Host "  Tài khoản demo khác: manager / Manager@123, sales1 / Sales@123, sales2 / Sales@123"
Write-Host "  Sao lưu: .\backup.ps1     Khôi phục: .\restore.ps1 <thư mục backup>     Gỡ: .\uninstall.ps1"
if (-not $NoBrowser) { Start-Process $url }
