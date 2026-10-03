#!/bin/sh
# Universal trampoline for standard Linux and Termux
if [ -z "$BASH_VERSION" ]; then
    if command -v bash >/dev/null 2>&1; then
        exec bash "$0" "$@"
    else
        echo "Error: bash is required." >&2
        exit 1
    fi
fi
set -e

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CERT_DIR="$DIR/runtime/certs"
mkdir -p "$CERT_DIR"

CN="${1:-localhost}"
IP="${2:-127.0.0.1}"

echo "=== Bot Agent SSL/TLS 证书生成与管理工具 ==="
echo "域名/主机名 (CN): $CN"
echo "IP 地址 (SAN): $IP"
echo "证书输出目录: $CERT_DIR"

# 优先使用 openssl（如果系统已安装）
if command -v openssl >/dev/null 2>&1; then
    echo "使用 OpenSSL 生成自签名证书..."
    openssl req -x509 -nodes -days 1095 -newkey rsa:2048 \
        -keyout "$CERT_DIR/botagent.key" \
        -out "$CERT_DIR/botagent.crt" \
        -subj "/CN=$CN/O=BotAgent" \
        -addext "subjectAltName=DNS:$CN,DNS:localhost,IP:$IP,IP:127.0.0.1" 2>/dev/null || \
    openssl req -x509 -nodes -days 1095 -newkey rsa:2048 \
        -keyout "$CERT_DIR/botagent.key" \
        -out "$CERT_DIR/botagent.crt" \
        -subj "/CN=$CN/O=BotAgent"

    openssl pkcs12 -export \
        -out "$CERT_DIR/botagent.pfx" \
        -inkey "$CERT_DIR/botagent.key" \
        -in "$CERT_DIR/botagent.crt" \
        -passout pass: 2>/dev/null || true
    echo "[+] 自签名证书生成成功:"
    echo "  - 证书公钥: $CERT_DIR/botagent.crt"
    echo "  - 证书私钥: $CERT_DIR/botagent.key"
    echo "  - PKCS#12:  $CERT_DIR/botagent.pfx"
else
    echo "未检测到 openssl，Bot Agent 启动时将自动通过 .NET 原生内核生成证书并保存至 $CERT_DIR。"
fi

echo ""
echo "--- Let's Encrypt 证书使用说明 ---"
echo "若您已有 Let's Encrypt 证书（由 certbot 或 acme.sh 签发），可在 .env 中添加："
echo "  QQCHAT_TLS_CERT=/etc/letsencrypt/live/yourdomain/fullchain.pem"
echo "  QQCHAT_TLS_KEY=/etc/letsencrypt/live/yourdomain/privkey.pem"
