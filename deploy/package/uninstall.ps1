<#
  Gỡ CRM.
    .\uninstall.ps1              Dừng và xóa container, GIỮ dữ liệu (cài lại sẽ thấy dữ liệu cũ)
    .\uninstall.ps1 -RemoveData  Xóa luôn database + file đính kèm (KHÔNG khôi phục được, nên chạy backup.ps1 trước)
#>
param([switch]$RemoveData, [switch]$Force)
Set-Location -Path $PSScriptRoot
if ($RemoveData) {
    if (-not $Force) {
        $answer = Read-Host "Xóa VĨNH VIỄN database và file đính kèm của CRM? Gõ YES để tiếp tục"
        if ($answer -ne "YES") { Write-Host "Đã hủy."; exit 0 }
    }
    & docker compose down -v
    Write-Host "[OK]  Đã gỡ CRM và xóa dữ liệu." -ForegroundColor Green
} else {
    & docker compose down
    Write-Host "[OK]  Đã gỡ CRM (dữ liệu vẫn được giữ trong Docker volume)." -ForegroundColor Green
}
