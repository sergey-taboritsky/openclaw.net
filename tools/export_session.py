#!/usr/bin/env python3
"""
OpenClaw 会话导出工具
从 FileMemoryStore 读取会话并导出为 Markdown 格式

用法:
  python tools/export_session.py <session-id> [--format json|md] [--output <path>]
  python tools/export_session.py websocket:0HNLVH2MP8540
  python tools/export_session.py websocket:0HNLVH2MP8540 --format json --output ./my_chat.json
"""

import argparse
import base64
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path


def encode_key(key: str) -> str:
    """将 session ID 编码为 FileMemoryStore 使用的 Base64 URL-safe 文件名."""
    if len(key) > 200:
        import hashlib
        h = hashlib.sha256(key.encode('utf-8')).digest()
        return base64.urlsafe_b64encode(h).rstrip(b'=').decode('ascii')
    return base64.urlsafe_b64encode(key.encode('utf-8')).rstrip(b'=').decode('ascii')


def find_memory_paths():
    """查找所有可能的 memory 存储路径."""
    candidates = []

    # 相对路径 (默认配置)
    candidates.append(Path("./memory"))

    # Gateway 项目目录
    candidates.append(Path("./src/OpenClaw.Gateway/memory"))

    # 用户目录
    candidates.append(Path.home() / ".openclaw" / "memory")

    # 通过环境变量
    ws = os.environ.get("OPENCLAW_WORKSPACE")
    if ws:
        candidates.append(Path(ws) / "memory")

    # 当前工作目录的各种可能
    for p in [Path.cwd(), Path.cwd() / "memory"]:
        if p not in candidates:
            candidates.append(p)

    return candidates


def find_session_file(session_id: str):
    """在文件系统中定位会话文件."""
    encoded = encode_key(session_id)
    filename = f"{encoded}.json"

    for base in find_memory_paths():
        session_file = base / "sessions" / filename
        if session_file.exists():
            return session_file

        # 也检查 SQLite
        sqlite_db = base / "openclaw.db"
        if sqlite_db.exists():
            return sqlite_db  # SQLite 需要特殊处理

    return None


def read_from_sqlite(db_path: Path, session_id: str):
    """从 SQLite 数据库读取会话."""
    try:
        import sqlite3
        conn = sqlite3.connect(str(db_path))
        cursor = conn.execute(
            "SELECT json FROM sessions WHERE id = ?", (session_id,)
        )
        row = cursor.fetchone()
        conn.close()
        if row:
            return json.loads(row[0])
    except ImportError:
        print("错误: 需要 sqlite3 模块读取数据库, 请安装 Python 3", file=sys.stderr)
    except Exception as e:
        print(f"SQLite 读取失败: {e}", file=sys.stderr)
    return None


def read_session(session_id: str):
    """读取会话数据."""
    location = find_session_file(session_id)

    if location is None:
        print(f"未找到会话 {session_id} 的存储文件。", file=sys.stderr)
        print("\n已搜索以下位置:", file=sys.stderr)
        for base in find_memory_paths():
            encoded = encode_key(session_id)
            target = base / "sessions" / f"{encoded}.json"
            marker = " [不存在]" if not target.exists() else " [存在]"
            print(f"  {target}{marker}", file=sys.stderr)
        print("\n可能原因:", file=sys.stderr)
        print("  1. Gateway 未启动或工作目录不同", file=sys.stderr)
        print("  2. 会话尚未持久化 (仍在内存中)", file=sys.stderr)
        print("  3. 使用了不同的 Memory.StoragePath 配置", file=sys.stderr)
        return None

    if location.suffix == ".db":
        return read_from_sqlite(location, session_id)
    else:
        with open(location, 'r', encoding='utf-8') as f:
            return json.load(f)


def format_timestamp(ts_str: str | None) -> str:
    """格式化时间戳."""
    if not ts_str:
        return ""
    try:
        dt = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
        return dt.strftime('%Y-%m-%d %H:%M:%S')
    except (ValueError, TypeError):
        return ts_str


def session_to_markdown(session_data: dict) -> str:
    """将会话数据转换为 Markdown."""
    lines = []
    sid = session_data.get('id', 'unknown')
    lines.append(f"# OpenClaw 会话: {sid}")
    lines.append(f"**导出时间**: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    lines.append(f"**Channel**: {session_data.get('channelId', 'N/A')}")
    lines.append(f"**Sender**: {session_data.get('senderId', 'N/A')}")

    last_active = session_data.get('lastActiveAt')
    if last_active:
        lines.append(f"**最后活跃**: {format_timestamp(last_active)}")

    lines.append("")
    lines.append("---")
    lines.append("")

    # 对话历史
    history = session_data.get('history', [])
    if not history:
        lines.append("*(此会话无对话历史)*")
        return "\n".join(lines)

    for i, msg in enumerate(history):
        role = msg.get('role', 'unknown')
        content = msg.get('content', '')
        timestamp = format_timestamp(msg.get('timestamp'))

        if role == 'user':
            lines.append(f"### 用户 ({timestamp})")
        elif role == 'assistant':
            lines.append(f"### Agent ({timestamp})")
        elif role == 'tool':
            lines.append(f"### 工具调用 ({timestamp})")
        else:
            lines.append(f"### {role} ({timestamp})")

        lines.append("")

        if isinstance(content, str):
            lines.append(content)
        elif isinstance(content, list):
            for block in content:
                if isinstance(block, dict):
                    if block.get('type') == 'text':
                        lines.append(block.get('text', ''))
                    elif block.get('type') == 'tool_use':
                        lines.append(f"```json\n{json.dumps(block, ensure_ascii=False, indent=2)}\n```")
                elif isinstance(block, str):
                    lines.append(block)
        else:
            lines.append(f"```json\n{json.dumps(content, ensure_ascii=False, indent=2)}\n```")

        lines.append("")
        lines.append("---")
        lines.append("")

    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(
        description="从 OpenClaw FileMemoryStore 导出会话",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
示例:
  python tools/export_session.py websocket:0HNLVH2MP8540
  python tools/export_session.py websocket:0HNLVH2MP8540 --format json
  python tools/export_session.py websocket:0HNLVH2MP8540 -o ./exports/chat.md
  python tools/export_session.py --list
        """
    )
    parser.add_argument('session_id', nargs='?', help='会话 ID (如 websocket:0HNLVH2MP8540)')
    parser.add_argument('--format', '-f', choices=['md', 'json'], default='md',
                        help='输出格式 (默认: md)')
    parser.add_argument('--output', '-o', help='输出文件路径 (默认: 标准输出)')
    parser.add_argument('--list', '-l', action='store_true',
                        help='列出所有可用的存储路径而不执行导出')

    args = parser.parse_args()

    if args.list:
        print("OpenClaw 会话存储路径扫描:\n")
        for base in find_memory_paths():
            sessions_dir = base / "sessions"
            status = "存在" if sessions_dir.exists() else "不存在"
            count = len(list(sessions_dir.glob("*.json"))) if sessions_dir.exists() else 0
            print(f"  {sessions_dir}  [{status}]  ({count} 个文件)")
            if sessions_dir.exists():
                for f in sorted(sessions_dir.glob("*.json")):
                    try:
                        data = json.loads(f.read_text('utf-8'))
                        sid = data.get('id', f.stem)
                        updated = format_timestamp(data.get('lastActiveAt', ''))
                        print(f"    -> {sid}  (更新: {updated})")
                    except Exception:
                        print(f"    -> {f.name}  (无法解析)")
        return

    if not args.session_id:
        parser.error("需要提供 session_id 或使用 --list")

    session = read_session(args.session_id)
    if session is None:
        sys.exit(1)

    if args.format == 'json':
        output = json.dumps(session, ensure_ascii=False, indent=2)
    else:
        output = session_to_markdown(session)

    if args.output:
        output_path = Path(args.output)
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(output, encoding='utf-8')
        print(f"会话已导出到: {output_path.absolute()}")
    else:
        print(output)


if __name__ == '__main__':
    main()
