#!/usr/bin/env bash
# Cài đặt CRM từ gói trên Linux (Docker Engine + docker compose plugin).
# Chạy:  ./install.sh [cổng] [số khách hàng mẫu] [Developer|Express]      VD: ./install.sh 8100 5 Express
# Chạy lại an toàn: chỉ nạp image mới và khởi động lại, KHÔNG xóa dữ liệu.
set -euo pipefail
cd "$(dirname "$0")"
info() { printf '\033[36m[CRM]\033[0m %s\n' "$1"; }
ok()   { printf '\033[32m[OK]\033[0m  %s\n' "$1"; }
fail() { printf '\033[31m[LỖI]\033[0m %s\n' "$1"; exit 1; }

docker version >/dev/null 2>&1 || fail "Docker chưa chạy (hoặc cần sudo / thêm user vào nhóm docker)."
docker compose version >/dev/null 2>&1 || fail "Thiếu docker compose plugin."
ok "Docker sẵn sàng"

VERSION="$(tr -d '[:space:]' < VERSION)"
PORT="${1:-0}"; SAMPLE="${2:-5}"; EDITION="${3:-Developer}"
case "$EDITION" in Developer|Express) ;; *) fail "Phiên bản SQL Server phải là Developer hoặc Express";; esac
port_free() { ! (ss -ltn 2>/dev/null || netstat -ltn 2>/dev/null) | awk '{print $4}' | grep -qE "[:.]$1\$"; }
if [ ! -f .env ]; then
  if [ "$PORT" = "0" ]; then PORT=8100; while ! port_free "$PORT"; do PORT=$((PORT+1)); done; fi
  RAND="$(tr -dc 'A-HJ-NP-Za-km-z2-9' < /dev/urandom | head -c 16)"
  cat > .env <<EOF
# Cấu hình CRM – giữ bí mật file này
CRM_VERSION=$VERSION
CRM_WEB_PORT=$PORT
CRM_SA_PASSWORD=Crm#${RAND}9a
# Số khách hàng mẫu khi khởi tạo database lần đầu (0 hoặc để trống = bộ mẫu mặc định 8 khách hàng)
CRM_SAMPLE_CUSTOMERS=$SAMPLE
# Phiên bản SQL Server: Developer (thử nghiệm) | Express (miễn phí, dùng thật, giới hạn 10GB)
CRM_SQL_EDITION=$EDITION
EOF
  chmod 600 .env
  ok "Đã tạo .env (cổng $PORT, $SAMPLE khách hàng mẫu)"
else
  sed -i "s/^CRM_VERSION=.*/CRM_VERSION=$VERSION/" .env
  ok "Giữ cấu hình .env hiện có, cập nhật phiên bản $VERSION"
fi
PORT="$(grep -E '^CRM_WEB_PORT=' .env | cut -d= -f2)"

for f in images/*.tar; do [ -e "$f" ] || continue; info "Nạp image $f..."; docker load -i "$f"; done
ok "Đã nạp image"

info "Khởi động CRM..."
docker compose up -d

info "Đợi ứng dụng khởi tạo (1–3 phút lần đầu)..."
for i in $(seq 1 72); do
  sleep 5
  if curl -fs -o /dev/null "http://localhost:$PORT/Account/Login"; then
    ok "CRM đã chạy: http://localhost:$PORT"
    echo "  Đăng nhập lần đầu: admin / Admin@123  (hãy ĐỔI MẬT KHẨU ngay)"
    exit 0
  fi
done
docker compose logs --tail 40 web
fail "Ứng dụng chưa phản hồi sau 6 phút."
