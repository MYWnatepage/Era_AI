# -*- coding: utf-8 -*-
"""
ai_bridge.py

Era_AI 文件桥接程序。

定位：
- Python 只做桥接，不维护游戏侧枚举表。
- 剧本名、角色名、风格名、风格提示词、测试回复等，由 Emuera 写入 ai_request.txt。
- 模型后端由 AI_CONFIG.txt 决定。
- 推荐 ai_request.txt 使用 JSON；额外兼容 Emuera 类 JSON 和旧的 KEY=VALUE|KEY=VALUE。

推荐 ai_request.txt JSON 示例：
{
  "prompt": "请以角色口吻回复玩家。",
  "reply_limit": 512,
  "memory_limit": 1024,
  "scenario_id": "rain_room",
  "scenario_name": "雨夜房间",
  "character_name": "艾拉",
  "style_code": "5",
  "style_name": "温柔",
  "style_prompt": "使用温柔风格，语气柔和，更照顾玩家感受。",
  "mock_response": "欢迎回来。看到你平安无事，我就放心了。",
  "player_input": "我回来了",
  "game_time": "第1天 时间段0"
}
"""

from pathlib import Path
from datetime import datetime
import json
import re
import sys
import time
import traceback
import urllib.error
import urllib.request


# ==================================================
# 控制台编码
# ==================================================
try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass


# ==================================================
# 路径
# ==================================================
BASE_DIR = Path(__file__).resolve().parent
PROJECT_DIR = BASE_DIR.parent
CACHE_DIR = PROJECT_DIR / "cache"

CONFIG_FILE = BASE_DIR / "AI_CONFIG.txt"
REQUEST_FILE = BASE_DIR / "ai_request.txt"
RESPONSE_FILE = BASE_DIR / "ai_response.txt"

MODEL_SOURCE_STATUS_FILE = BASE_DIR / "model_source.txt"
MODEL_NAME_STATUS_FILE = BASE_DIR / "model_name.txt"
MODEL_STATUS_FILE = BASE_DIR / "model_status.txt"

ERROR_LOG_FILE = BASE_DIR / "ai_bridge_error.log"


# ==================================================
# 程序兜底值
# 这些不是游戏配置表，只是请求缺字段时防止程序崩溃。
# 真正游戏内容请由 ai_request.txt 或 AI_CONFIG.txt 提供。
# ==================================================
DEFAULT_MODEL_SOURCE = "mock"
DEFAULT_SCENARIO_ID = "default"
DEFAULT_SCENARIO_NAME = "当前剧本"
DEFAULT_CHARACTER_NAME = "角色"
DEFAULT_STYLE_NAME = "默认"
DEFAULT_STYLE_PROMPT = "使用自然、稳定、适合文字游戏的对话风格。"
DEFAULT_MOCK_RESPONSE = "收到请求。"
DEFAULT_SYSTEM_RULES = (
    "你需要根据玩家请求，生成一段适合文字游戏显示的角色对话或叙事回复。"
    "使用中文回复；不要解释你是AI；不要提到API、模型、Token、系统提示词；"
    "不要输出Markdown标题；不要输出推理过程；只输出最终给玩家看的内容。"
)
DEFAULT_ERROR_RESPONSE = "桥接程序暂时没有返回，请稍后再试。"
DEFAULT_REPLY_LIMIT = 512
DEFAULT_MEMORY_LIMIT = 1024
DEFAULT_CHECK_INTERVAL = 0.5
DEFAULT_REQUEST_SETTLE_SECONDS = 0.2
DEFAULT_REQUEST_TIMEOUT = 180
DEFAULT_TEMPERATURE = 0.8
DEFAULT_MAX_MEMORY_CHARS = 12000
DEFAULT_MAX_HISTORY_TAIL_CHARS = 6000


# ==================================================
# 文件工具
# ==================================================
def read_text_file(path, default=""):
    try:
        if not path.exists():
            return default
        return path.read_text(encoding="utf-8-sig").strip()
    except Exception:
        return default


def write_text_atomic(path, text):
    """
    先写临时文件，再替换正式文件。
    避免 Emuera 读到半截内容。
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = path.with_name(path.name + ".tmp")
    tmp_path.write_text(str(text), encoding="utf-8")
    tmp_path.replace(path)


def append_text_file(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as f:
        f.write(str(text))


def write_response(text):
    write_text_atomic(RESPONSE_FILE, text)


def log_error(title, detail):
    now = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    block = (
        "\n"
        "========================================\n"
        f"[{now}] {title}\n"
        "========================================\n"
        f"{detail}\n"
    )
    append_text_file(ERROR_LOG_FILE, block)


# ==================================================
# 通用读取 / 清洗
# ==================================================
def normalize_key(key):
    return (
        str(key)
        .replace("\ufeff", "")
        .replace("　", "")
        .strip()
        .upper()
    )


def normalize_value(value):
    if value is None:
        return ""

    if isinstance(value, (dict, list)):
        return json.dumps(value, ensure_ascii=False)

    return (
        str(value)
        .replace("\ufeff", "")
        .replace("　", " ")
        .strip()
    )


def get_value(data, *keys, default=""):
    for key in keys:
        value = data.get(normalize_key(key), "")
        if value != "":
            return value
    return default


def safe_int(value, default_value, min_value, max_value):
    try:
        number = int(str(value).strip())
    except Exception:
        return default_value

    if number < min_value:
        return min_value

    if number > max_value:
        return max_value

    return number


def safe_float(value, default_value, min_value, max_value):
    try:
        number = float(str(value).strip())
    except Exception:
        return default_value

    if number < min_value:
        return min_value

    if number > max_value:
        return max_value

    return number


# ==================================================
# AI_CONFIG.txt
# ==================================================
def read_key_value_file(path):
    data = {}
    text = read_text_file(path, default="")

    for raw_line in text.splitlines():
        line = raw_line.strip()

        if not line:
            continue

        if line.startswith("#") or line.startswith(";"):
            continue

        if "=" not in line:
            continue

        key, value = line.split("=", 1)
        data[normalize_key(key)] = normalize_value(value)

    return data


def read_config():
    return read_key_value_file(CONFIG_FILE)


def get_config_value(config, *keys, default=""):
    return get_value(config, *keys, default=default)


# ==================================================
# ai_request.txt 解析
# ==================================================
def read_request():
    return read_text_file(REQUEST_FILE, default="")


def parse_json_request(text):
    """
    推荐格式。JSON 能避免玩家输入里的 | 和 = 破坏字段解析。
    """
    try:
        obj = json.loads(text)
    except Exception:
        return None

    if not isinstance(obj, dict):
        return None

    data = {}

    for key, value in obj.items():
        data[normalize_key(key)] = normalize_value(value)

    return data


def parse_pipe_request(text):
    """
    兼容旧格式：
        PROMPT=xxx|REPLY_LIMIT=512|STYLE_NAME=温柔

    注意：如果玩家输入里包含 |KEY= 形态，旧格式仍可能被切开。
    所以正式版本建议改 ERB，改用 JSON 写 ai_request.txt。
    """
    data = {}

    pattern = re.compile(r"(^|\|)([A-Za-z][A-Za-z0-9_]{1,80})=")
    matches = list(pattern.finditer(text))

    if not matches:
        if "=" in text:
            key, value = text.split("=", 1)
            data[normalize_key(key)] = normalize_value(value)
        return data

    for index, match in enumerate(matches):
        key = normalize_key(match.group(2))
        value_start = match.end()

        if index + 1 < len(matches):
            value_end = matches[index + 1].start()
        else:
            value_end = len(text)

        data[key] = normalize_value(text[value_start:value_end])

    return data


def parse_loose_json_request(text):
    """
    兼容 Emuera 写出的“类 JSON”单行请求。

    用途：
    - 严格 JSON 解析失败时兜底。
    - 支持玩家输入中出现英文双引号和反斜杠。
    - 依靠字段标记边界切分，而不是依靠严格 JSON 字符串转义。
    """
    data = {}

    if not text:
        return None

    raw = str(text).replace("\ufeff", "").strip()

    if not raw.startswith("{") or not raw.endswith("}"):
        return None

    key_pattern = re.compile(r'(^|{|,)\s*"([A-Za-z][A-Za-z0-9_]{0,80})"\s*:')
    matches = list(key_pattern.finditer(raw))

    if not matches:
        return None

    for index, match in enumerate(matches):
        key = normalize_key(match.group(2))
        value_start = match.end()

        if index + 1 < len(matches):
            value_end = matches[index + 1].start()
        else:
            value_end = len(raw) - 1

        value = raw[value_start:value_end].strip()

        if value.startswith('"') and value.endswith('"'):
            value = value[1:-1]
        elif value.startswith('"'):
            value = value[1:]

        # 轻量反转义。
        value = value.replace('\\"', '"')
        value = value.replace('\\\\', '\\')

        data[key] = normalize_value(value)

    return data

def parse_request(text):
    text = str(text or "").replace("\ufeff", "").strip()

    if not text:
        return {}

    # 第一优先级：严格 JSON。
    json_data = parse_json_request(text)

    if json_data is not None:
        return json_data

    # 第二优先级：Emuera 类 JSON。
    # 用于兼容玩家输入中包含英文双引号或反斜杠的情况。
    loose_json_data = parse_loose_json_request(text)

    if loose_json_data is not None:
        return loose_json_data

    # 第三优先级：旧版 KEY=VALUE|KEY=VALUE。
    return parse_pipe_request(text)


# ==================================================
# 请求字段
# ==================================================
def get_request_text(data, *keys, default=""):
    return get_value(data, *keys, default=default)


def sanitize_file_id(value):
    value = str(value or DEFAULT_SCENARIO_ID).strip().lower()
    allowed = []

    for ch in value:
        if ch.isalnum() or ch in ["_", "-"]:
            allowed.append(ch)

    result = "".join(allowed)
    return result or DEFAULT_SCENARIO_ID


def get_scenario_id(data, config=None):
    config = config or {}
    value = get_request_text(
        data,
        "SCENARIO_ID",
        default=get_config_value(config, "DEFAULT_SCENARIO_ID", default=DEFAULT_SCENARIO_ID),
    )
    return sanitize_file_id(value)


def get_scenario_name(data, config=None):
    config = config or {}
    return get_request_text(
        data,
        "SCENARIO_NAME",
        "STORY_NAME",
        default=get_config_value(config, "DEFAULT_SCENARIO_NAME", default=DEFAULT_SCENARIO_NAME),
    )


def get_character_name(data, config=None):
    config = config or {}
    return get_request_text(
        data,
        "CHARACTER_NAME",
        "ROLE_NAME",
        "NPC_NAME",
        default=get_config_value(config, "DEFAULT_CHARACTER_NAME", default=DEFAULT_CHARACTER_NAME),
    )


def get_style_info(data, config=None):
    config = config or {}

    return {
        "code": get_request_text(data, "STYLE_CODE", default="0"),
        "name": get_request_text(
            data,
            "STYLE_NAME",
            "DIALOGUE_STYLE_NAME",
            default=get_config_value(config, "DEFAULT_STYLE_NAME", default=DEFAULT_STYLE_NAME),
        ),
        "prompt": get_request_text(
            data,
            "STYLE_PROMPT",
            "DIALOGUE_STYLE_PROMPT",
            default=get_config_value(config, "DEFAULT_STYLE_PROMPT", default=DEFAULT_STYLE_PROMPT),
        ),
        "mock_response": get_request_text(
            data,
            "MOCK_RESPONSE",
            default=get_config_value(config, "DEFAULT_MOCK_RESPONSE", default=DEFAULT_MOCK_RESPONSE),
        ),
    }


# ==================================================
# cache 长期记忆
# ==================================================
def get_cache_files(scenario_id):
    scenario_id = sanitize_file_id(scenario_id)
    return (
        CACHE_DIR / f"{scenario_id}_memory.txt",
        CACHE_DIR / f"{scenario_id}_history.txt",
        CACHE_DIR / f"{scenario_id}_meta.txt",
    )


def ensure_cache_files(scenario_id, scenario_name):
    CACHE_DIR.mkdir(parents=True, exist_ok=True)

    memory_file, history_file, meta_file = get_cache_files(scenario_id)

    if not memory_file.exists():
        write_text_atomic(memory_file, f"{scenario_name}长期记忆：\n")

    if not history_file.exists():
        write_text_atomic(history_file, "")

    if not meta_file.exists():
        write_text_atomic(meta_file, "TOTAL_TURNS=0\n")

    return memory_file, history_file, meta_file


def clip_tail(text, max_chars):
    text = str(text or "").strip()

    if max_chars <= 0:
        return ""

    if len(text) <= max_chars:
        return text

    return text[-max_chars:]


def compact_text(text, max_chars):
    text = str(text or "")
    text = text.replace("\r", " ").replace("\n", " ").strip()

    while "  " in text:
        text = text.replace("  ", " ")

    if len(text) <= max_chars:
        return text

    return text[:max_chars] + "……"


def get_memory_limits(config):
    max_memory_chars = safe_int(
        get_config_value(config, "MAX_MEMORY_CHARS", default=DEFAULT_MAX_MEMORY_CHARS),
        DEFAULT_MAX_MEMORY_CHARS,
        1000,
        200000,
    )

    max_history_tail_chars = safe_int(
        get_config_value(config, "MAX_HISTORY_TAIL_CHARS", default=DEFAULT_MAX_HISTORY_TAIL_CHARS),
        DEFAULT_MAX_HISTORY_TAIL_CHARS,
        0,
        100000,
    )

    return max_memory_chars, max_history_tail_chars


def read_long_memory(scenario_id, scenario_name, memory_limit, config):
    if memory_limit <= 0:
        return ""

    memory_file, history_file, meta_file = ensure_cache_files(scenario_id, scenario_name)
    max_memory_chars, max_history_tail_chars = get_memory_limits(config)

    # 粗略估算：1 token ≈ 2 个中文字符。
    max_chars_from_token_limit = max(400, memory_limit * 2)

    memory_text = read_text_file(memory_file, default="")
    history_text = read_text_file(history_file, default="")

    memory_part = clip_tail(memory_text, min(max_memory_chars, max_chars_from_token_limit))
    history_part = clip_tail(history_text, min(max_history_tail_chars, max_chars_from_token_limit))

    parts = []

    if memory_part:
        parts.append("【长期记忆摘要】\n" + memory_part)

    if history_part:
        parts.append("【外部对话记录摘录】\n" + history_part)

    return "\n\n".join(parts).strip()


def get_total_turns(meta_file):
    meta_text = read_text_file(meta_file, default="")

    for line in meta_text.splitlines():
        if line.startswith("TOTAL_TURNS="):
            try:
                return int(line.split("=", 1)[1].strip())
            except Exception:
                return 0

    return 0


def append_external_history(data, config, ai_text, backend_name):
    scenario_id = get_scenario_id(data, config)
    scenario_name = get_scenario_name(data, config)
    character_name = get_character_name(data, config)

    memory_file, history_file, meta_file = ensure_cache_files(scenario_id, scenario_name)

    now = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    game_time = get_request_text(data, "GAME_TIME", default="未知时间")
    player_input = get_request_text(data, "PLAYER_INPUT", default="（PLAYER_INPUT 未提供）")

    if not ai_text:
        ai_text = "（未记录回应）"

    block = (
        f"[{now}] 剧本：{scenario_name} 游戏时间：{game_time} 来源：{backend_name}\n"
        f"玩家：{player_input}\n"
        f"{character_name}：{ai_text}\n"
        "\n"
    )

    append_text_file(history_file, block)

    total_turns = get_total_turns(meta_file) + 1
    meta_text = f"TOTAL_TURNS={total_turns}\nLAST_UPDATE={now}\n"
    write_text_atomic(meta_file, meta_text)

    update_long_memory(
        memory_file=memory_file,
        scenario_name=scenario_name,
        character_name=character_name,
        total_turns=total_turns,
        game_time=game_time,
        player_input=player_input,
        ai_text=ai_text,
        config=config,
    )


def update_long_memory(memory_file, scenario_name, character_name, total_turns, game_time, player_input, ai_text, config):
    old_text = read_text_file(memory_file, default="")

    if not old_text:
        old_text = f"{scenario_name}长期记忆：\n"

    player_input = compact_text(player_input, 300)
    ai_text = compact_text(ai_text, 500)

    entry = (
        f"- 第{total_turns}轮（{game_time}）："
        f"玩家说「{player_input}」；"
        f"{character_name}回应「{ai_text}」。"
    )

    new_text = (old_text.rstrip() + "\n" + entry).strip()

    max_memory_chars, _ = get_memory_limits(config)

    if len(new_text) > max_memory_chars:
        header = f"{scenario_name}长期记忆：\n"
        body_limit = max(500, max_memory_chars - len(header))
        new_text = header + new_text[-body_limit:]

    write_text_atomic(memory_file, new_text)


# ==================================================
# 模型状态与后端配置
# ==================================================
def get_model_source(config):
    return get_config_value(config, "MODEL_SOURCE", default=DEFAULT_MODEL_SOURCE).lower()


def get_runtime_model_info(config):
    source = get_model_source(config)

    source_name = get_config_value(
        config,
        "MODEL_SOURCE_DISPLAY_NAME",
        "MODEL_SOURCE_NAME",
        default=source.upper(),
    )

    prefix = source.upper()

    model_name = get_config_value(
        config,
        "MODEL_DISPLAY_NAME",
        "MODEL_NAME",
        f"{prefix}_MODEL_NAME",
        "OPENAI_MODEL",
        "GX_MODEL_NAME",
        "MOCK_MODEL_NAME",
        default="未读取",
    )

    return source_name, model_name


def write_runtime_status(config, extra_text=""):
    source_name, model_name = get_runtime_model_info(config)
    now = datetime.now().strftime("%Y-%m-%d %H:%M:%S")

    status_text = (
        f"MODEL_SOURCE={source_name}\n"
        f"MODEL_NAME={model_name}\n"
        f"UPDATED_AT={now}\n"
    )

    if extra_text:
        status_text += f"STATUS={extra_text}\n"

    try:
        write_text_atomic(MODEL_SOURCE_STATUS_FILE, source_name)
        write_text_atomic(MODEL_NAME_STATUS_FILE, model_name)
        write_text_atomic(MODEL_STATUS_FILE, status_text)
    except Exception:
        pass


def get_backend_api_config(config, source):
    prefix = source.upper()

    model_name = get_config_value(
        config,
        "MODEL_NAME",
        f"{prefix}_MODEL_NAME",
        "OPENAI_MODEL",
        "GX_MODEL_NAME",
        default="",
    )

    base_url = get_config_value(
        config,
        "API_BASE_URL",
        f"{prefix}_API_BASE_URL",
        "OPENAI_API_BASE_URL",
        "GX_API_BASE_URL",
        default="",
    )

    api_key = get_config_value(
        config,
        "API_KEY",
        f"{prefix}_API_KEY",
        "OPENAI_API_KEY",
        "GX_API_KEY",
        default="",
    )

    return model_name, base_url, api_key


def validate_config(config):
    source = get_model_source(config)

    if source == "mock":
        return ""

    if source not in BACKEND_HANDLERS:
        return "AI_CONFIG.txt 中 MODEL_SOURCE 不在当前支持范围内。"

    model_name, base_url, api_key = get_backend_api_config(config, source)

    if not model_name:
        return f"AI_CONFIG.txt 中 {source.upper()} 模型名称为空。"

    if not base_url:
        return f"AI_CONFIG.txt 中 {source.upper()} API_BASE_URL 为空。"

    if source == "openai":
        if not api_key:
            return "AI_CONFIG.txt 中 OPENAI_API_KEY 为空。"

        if api_key in ["你的OpenAI API Key", "YOUR_API_KEY", "sk-xxxx"]:
            return "AI_CONFIG.txt 中 OPENAI_API_KEY 还没有替换成真实密钥。"

    return ""


# ==================================================
# 提示词
# ==================================================
def build_prompt_parts(data, config):
    prompt = get_request_text(data, "PROMPT", default="")

    reply_limit = safe_int(
        get_request_text(data, "REPLY_LIMIT", default=DEFAULT_REPLY_LIMIT),
        DEFAULT_REPLY_LIMIT,
        64,
        4096,
    )

    memory_limit = safe_int(
        get_request_text(data, "MEMORY_LIMIT", default=DEFAULT_MEMORY_LIMIT),
        DEFAULT_MEMORY_LIMIT,
        0,
        8192,
    )

    scenario_id = get_scenario_id(data, config)
    scenario_name = get_scenario_name(data, config)
    character_name = get_character_name(data, config)
    style = get_style_info(data, config)

    long_memory_text = read_long_memory(
        scenario_id=scenario_id,
        scenario_name=scenario_name,
        memory_limit=memory_limit,
        config=config,
    )

    if not long_memory_text:
        long_memory_text = "暂无可用长期记忆。"

    system_rules = get_request_text(
        data,
        "SYSTEM_RULES",
        "SYSTEM_PROMPT",
        default=get_config_value(config, "DEFAULT_SYSTEM_RULES", default=DEFAULT_SYSTEM_RULES),
    )

    system_prompt = f"""
{system_rules}

当前剧本：
{scenario_name}

当前角色：
{character_name}

当前对话风格：
{style["name"]}

风格要求：
{style["prompt"]}

当前参数：
- 回复上限：{reply_limit} Token
- 记忆容量：{memory_limit} Token
- 剧本ID：{scenario_id}

可用长期记忆：
{long_memory_text}
""".strip()

    return {
        "system_prompt": system_prompt,
        "user_prompt": prompt,
        "reply_limit": reply_limit,
        "memory_limit": memory_limit,
        "style": style,
        "scenario_id": scenario_id,
        "scenario_name": scenario_name,
        "character_name": character_name,
    }


# ==================================================
# 后端
# ==================================================
def call_mock_backend(config, data):
    parts = build_prompt_parts(data, config)

    if not parts["user_prompt"]:
        return "没有读取到 PROMPT 字段，请检查 AI/ai_request.txt 的格式。"

    return parts["style"]["mock_response"]


def call_chat_completions(config, data, backend_name):
    source = get_model_source(config)
    model_name, base_url, api_key = get_backend_api_config(config, source)
    parts = build_prompt_parts(data, config)

    if not parts["user_prompt"]:
        return "没有读取到 PROMPT 字段，请检查 AI/ai_request.txt 的格式。"

    timeout_seconds = safe_int(
        get_config_value(config, "REQUEST_TIMEOUT", "TIMEOUT_SECONDS", default=DEFAULT_REQUEST_TIMEOUT),
        DEFAULT_REQUEST_TIMEOUT,
        5,
        600,
    )

    temperature = safe_float(
        get_config_value(config, "TEMPERATURE", default=DEFAULT_TEMPERATURE),
        DEFAULT_TEMPERATURE,
        0.0,
        2.0,
    )

    endpoint = base_url.rstrip("/") + "/chat/completions"

    payload = {
        "model": model_name,
        "messages": [
            {
                "role": "system",
                "content": parts["system_prompt"],
            },
            {
                "role": "user",
                "content": parts["user_prompt"],
            },
        ],
        "max_tokens": parts["reply_limit"],
        "temperature": temperature,
        "stream": False,
    }

    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")

    headers = {
        "Content-Type": "application/json",
    }

    if api_key and api_key.upper() not in ["EMPTY", "NONE", "NULL"]:
        headers["Authorization"] = "Bearer " + api_key

    request = urllib.request.Request(
        endpoint,
        data=body,
        headers=headers,
        method="POST",
    )

    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            response_text = response.read().decode("utf-8", errors="replace")

    except urllib.error.HTTPError as e:
        error_body = e.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"{backend_name} HTTPError {e.code}: {error_body}")

    except urllib.error.URLError as e:
        raise RuntimeError(f"{backend_name} URLError: {e}")

    try:
        result = json.loads(response_text)
    except Exception:
        raise RuntimeError(f"{backend_name} 返回内容不是 JSON：{response_text}")

    choices = result.get("choices", [])

    if not choices:
        raise RuntimeError(f"{backend_name} 返回结果中没有 choices 字段：{response_text}")

    message = choices[0].get("message", {})
    content = message.get("content", "")

    if content is None:
        content = ""

    content = str(content).strip()

    if not content:
        raise RuntimeError(f"{backend_name} 返回 content 为空。原始返回：{response_text}")

    return content


def call_gx_backend(config, data):
    return call_chat_completions(config, data, "GX")


def call_openai_backend(config, data):
    return call_chat_completions(config, data, "OpenAI")


BACKEND_HANDLERS = {
    "mock": call_mock_backend,
    "gx": call_gx_backend,
    "openai": call_openai_backend,
}


def call_selected_backend(config, data):
    source = get_model_source(config)
    handler = BACKEND_HANDLERS.get(source)

    if handler is None:
        return "unknown", f"未知 MODEL_SOURCE：{source}"

    return source, handler(config, data)


# ==================================================
# 请求处理
# ==================================================
def make_error_response(config):
    return get_config_value(config, "DEFAULT_ERROR_RESPONSE", default=DEFAULT_ERROR_RESPONSE)


def handle_request():
    request_text = read_request()

    if not request_text:
        write_response("没有读取到请求内容。")
        print("[WARN] 请求文件为空。")
        return

    data = parse_request(request_text)
    config = read_config()

    write_runtime_status(config)

    config_error = validate_config(config)

    if config_error:
        write_runtime_status(config, config_error)
        log_error("AI_CONFIG.txt 配置错误", config_error)
        write_response(make_error_response(config))
        print("[CONFIG ERROR]", config_error)
        return

    try:
        backend_name, ai_text = call_selected_backend(config, data)

        append_external_history(
            data=data,
            config=config,
            ai_text=ai_text,
            backend_name=backend_name,
        )

        debug_output = get_config_value(config, "DEBUG_OUTPUT", default="false").lower()

        if debug_output in ["1", "true", "yes", "on"]:
            now = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
            final_response = (
                "【AI回复】\n"
                f"时间：{now}\n"
                f"来源：{backend_name}\n\n"
                f"{ai_text}"
            )
        else:
            final_response = ai_text

        write_runtime_status(config, f"last_backend={backend_name}")
        write_response(final_response)

        scenario_id = get_scenario_id(data, config)
        print(
            f"[OK] {datetime.now().strftime('%H:%M:%S')} "
            f"已完成一次 {backend_name} 请求，并写入 cache/{scenario_id}_*.txt。"
        )

    except Exception as e:
        error_detail = traceback.format_exc()
        log_error(type(e).__name__, error_detail)
        write_response(make_error_response(config))
        print("[ERROR]", type(e).__name__, e)


# ==================================================
# 监听循环
# ==================================================
def get_request_mtime_ns():
    if not REQUEST_FILE.exists():
        return None

    return REQUEST_FILE.stat().st_mtime_ns


def get_loop_settings(config):
    check_interval = safe_float(
        get_config_value(config, "CHECK_INTERVAL", default=DEFAULT_CHECK_INTERVAL),
        DEFAULT_CHECK_INTERVAL,
        0.1,
        10.0,
    )

    settle_seconds = safe_float(
        get_config_value(config, "REQUEST_SETTLE_SECONDS", default=DEFAULT_REQUEST_SETTLE_SECONDS),
        DEFAULT_REQUEST_SETTLE_SECONDS,
        0.0,
        5.0,
    )

    return check_interval, settle_seconds


def print_startup_info(config):
    print("========================================")
    print("AI Bridge 已启动。")
    print("正在监听：AI/ai_request.txt")
    print("输出文件：AI/ai_response.txt")
    print("错误日志：AI/ai_bridge_error.log")
    print("长期记忆：cache/<scenario>_memory.txt")
    print("按 Ctrl + C 可以停止。")
    print("========================================")

    source = get_model_source(config)
    config_error = validate_config(config)

    if config_error:
        print("[CONFIG ERROR]", config_error)
        print("请先检查 AI/AI_CONFIG.txt。")
    else:
        source_name, model_name = get_runtime_model_info(config)
        print(f"[CONFIG] MODEL_SOURCE={source}")
        print(f"[CONFIG] DISPLAY_SOURCE={source_name}")
        print(f"[CONFIG] DISPLAY_MODEL={model_name}")

        if source != "mock":
            model_name_raw, base_url, api_key = get_backend_api_config(config, source)
            print(f"[CONFIG] MODEL_NAME={model_name_raw}")
            print(f"[CONFIG] API_BASE_URL={base_url}")

            if api_key:
                print("[CONFIG] API_KEY=已读取，不显示。")

        else:
            print("[CONFIG] 使用 mock 测试后端，不调用真实模型。")

    print("========================================")


def main():
    config = read_config()
    write_runtime_status(config)
    print_startup_info(config)

    check_interval, settle_seconds = get_loop_settings(config)
    last_mtime = get_request_mtime_ns()

    while True:
        try:
            current_mtime = get_request_mtime_ns()

            if current_mtime is None:
                time.sleep(check_interval)
                continue

            if last_mtime is None:
                last_mtime = current_mtime

            elif current_mtime != last_mtime:
                last_mtime = current_mtime

                if settle_seconds > 0:
                    time.sleep(settle_seconds)

                handle_request()

                # 允许每次请求后更新循环参数。
                config = read_config()
                check_interval, settle_seconds = get_loop_settings(config)

            time.sleep(check_interval)

        except KeyboardInterrupt:
            print("\nAI Bridge 已停止。")
            break

        except Exception as e:
            config = read_config()
            error_detail = traceback.format_exc()
            log_error(type(e).__name__, error_detail)
            write_response(make_error_response(config))
            print("[ERROR]", type(e).__name__, e)
            time.sleep(1.0)


if __name__ == "__main__":
    main()
