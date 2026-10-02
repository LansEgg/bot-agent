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

if [ ! -f "$PID_FILE" ]; then
    echo "Bot Agent 未运行（未找到 PID 文件）"
    exit 0
fi

PID=$(cat "$PID_FILE" 2>/dev/null || true)
if [ -z "$PID" ]; then
    rm -f "$PID_FILE"
    echo "PID 文件为空，已清除"
    exit 0
fi

if ! kill -0 "$PID" 2>/dev/null; then
    echo "进程 (PID: $PID) 已经不在运行"
    rm -f "$PID_FILE"
    exit 0
fi

echo "正在停止 Bot Agent (PID: $PID)..."
kill -TERM "$PID" 2>/dev/null || true

# 等待优雅收尾（落盘 SQLite）
for i in {1..10}; do
    if ! kill -0 "$PID" 2>/dev/null; then
        echo "✅ Bot Agent 已正常停止"
        rm -f "$PID_FILE"
        exit 0
    fi
    sleep 1
done

echo "⚠️ 进程未响应，正在强制结束..."
kill -KILL "$PID" 2>/dev/null || true
rm -f "$PID_FILE"
echo "已强制结束"
