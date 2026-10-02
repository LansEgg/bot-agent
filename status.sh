#!/bin/sh
# Universal trampoline for standard Linux (Ubuntu, Debian, CentOS, etc.) and Termux
if [ -z "$BASH_VERSION" ]; then
    if command -v bash >/dev/null 2>&1; then
        exec bash "$0" "$@"
    else
        echo "Error: bash is required." >&2
        exit 1
    fi
fi

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PID_FILE="$DIR/runtime/bot-agent.pid"
LOG_FILE="$DIR/runtime/logs/bot-agent.log"

echo "=== Bot Agent 运行状态 ==="
if [ -f "$PID_FILE" ]; then
    PID=$(cat "$PID_FILE" 2>/dev/null || true)
    if [ -n "$PID" ] && kill -0 "$PID" 2>/dev/null; then
        echo "● 状态: 运行中 (PID: $PID)"
    else
        echo "○ 状态: 未运行 (PID 文件残留)"
    fi
else
    echo "○ 状态: 未运行"
fi

PORT="${QQCHAT_HEALTH_PORT:-8080}"
echo ""
echo "=== HTTP 探针 (端口: $PORT) ==="
HEALTH=$(curl -s -m 2 "http://127.0.0.1:${PORT}/healthz" 2>/dev/null || true)
if [ -n "$HEALTH" ]; then
    echo "✓ 存活探针 /healthz: $HEALTH"
else
    echo "✗ 存活探针 /healthz: 无法连接"
fi

READY=$(curl -s -m 2 "http://127.0.0.1:${PORT}/readyz" 2>/dev/null || true)
if [ -n "$READY" ]; then
    echo "✓ 就绪探针 /readyz: $READY"
else
    echo "✗ 就绪探针 /readyz: 未就绪 (尚未连接到 OneBot 协议端或服务未起)"
fi

if [ -f "$LOG_FILE" ]; then
    echo ""
    echo "=== 最近 10 行日志 ==="
    tail -n 10 "$LOG_FILE"
fi
