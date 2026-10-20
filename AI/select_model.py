# -*- coding: utf-8 -*-
"""
select_model.py

启动前模型配置选择器。

功能：
1. 读取 AI/model_profiles.ini
2. 显示常用模型配置列表
3. 用户选择一个配置
4. 自动生成 AI/AI_CONFIG.txt
5. 写入 AI/model_selected.txt，方便排错查看

使用方式：
    python AI/select_model.py

说明：
    本脚本只负责生成 AI_CONFIG.txt。
    ai_bridge.py 仍然只读取 AI_CONFIG.txt。
"""

from pathlib import Path
import configparser
import sys
from datetime import datetime


try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stdin.reconfigure(encoding="utf-8")
except Exception:
    pass


BASE_DIR = Path(__file__).resolve().parent
PROFILE_FILE = BASE_DIR / "model_profiles.ini"
CONFIG_FILE = BASE_DIR / "AI_CONFIG.txt"
SELECTED_FILE = BASE_DIR / "model_selected.txt"


def ensure_default_profiles():
    if PROFILE_FILE.exists():
        return

    default_text = """# model_profiles.ini
# 常用模型配置列表

[MOCK]
DISPLAY_NAME=mock 测试模式
MODEL_SOURCE=mock
MOCK_MODEL_NAME=mock-test

[GX_DEEPSEEK]
DISPLAY_NAME=GX - deepseek-v4-flash
MODEL_SOURCE=gx
GX_MODEL_NAME=deepseek-v4-flash
GX_API_BASE_URL=http://172.16.16.20:8000/v1
GX_API_KEY=EMPTY

[OPENAI_MINI]
DISPLAY_NAME=OpenAI - gpt-4.1-mini
MODEL_SOURCE=openai
OPENAI_MODEL=gpt-4.1-mini
OPENAI_API_BASE_URL=https://api.openai.com/v1
OPENAI_API_KEY=
"""
    PROFILE_FILE.write_text(default_text, encoding="utf-8")


def read_profiles():
    ensure_default_profiles()

    parser = configparser.ConfigParser()
    parser.optionxform = str
    parser.read(PROFILE_FILE, encoding="utf-8-sig")

    profiles = []
    for section in parser.sections():
        item = dict(parser.items(section))
        display_name = item.get("DISPLAY_NAME", section)
        profiles.append((section, display_name, item))

    return profiles


def write_ai_config(section, display_name, item):
    lines = [
        "# AI_CONFIG.txt",
        "# 本文件由 AI/select_model.py 自动生成。",
        "# 如需修改常用模型，请编辑 AI/model_profiles.ini。",
        f"# SELECTED_PROFILE={section}",
        f"# DISPLAY_NAME={display_name}",
        f"# GENERATED_AT={datetime.now().strftime('%Y-%m-%d %H:%M:%S')}",
        "",
    ]

    for key, value in item.items():
        if key == "DISPLAY_NAME":
            continue
        lines.append(f"{key}={value}")

    lines.append("")
    CONFIG_FILE.write_text("\n".join(lines), encoding="utf-8")

    selected_text = (
        f"SELECTED_PROFILE={section}\n"
        f"DISPLAY_NAME={display_name}\n"
        f"GENERATED_AT={datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n"
        f"CONFIG_FILE={CONFIG_FILE}\n"
    )
    SELECTED_FILE.write_text(selected_text, encoding="utf-8")


def main():
    profiles = read_profiles()

    if not profiles:
        print("[ERROR] 没有读取到任何模型配置。")
        print(f"请检查：{PROFILE_FILE}")
        return 1

    print("========================================")
    print("Era_AI 模型配置选择")
    print("========================================")
    print()
    print("请选择本次启动使用的模型：")
    print()

    for index, (section, display_name, item) in enumerate(profiles, start=1):
        model_source = item.get("MODEL_SOURCE", "unknown")
        print(f"[{index}] {display_name}    ({model_source})")

    print()
    print("[0] 取消启动")
    print()

    while True:
        choice = input("请输入编号：").strip()

        if choice == "0":
            print("已取消启动。")
            return 2

        if not choice.isdigit():
            print("输入无效，请输入数字。")
            continue

        index = int(choice)
        if index < 1 or index > len(profiles):
            print("编号超出范围，请重新输入。")
            continue

        section, display_name, item = profiles[index - 1]
        write_ai_config(section, display_name, item)

        print()
        print("已生成 AI_CONFIG.txt")
        print(f"当前配置：{display_name}")
        print(f"配置文件：{CONFIG_FILE}")
        print()
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
