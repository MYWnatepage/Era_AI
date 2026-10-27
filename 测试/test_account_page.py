from test_support import TEST_ROOT, GAME_DIR, GAME_EXE
from pathlib import Path
from http.server import HTTPServer, BaseHTTPRequestHandler
import json
import shutil
import subprocess
import threading
import time
import uuid

calls = []
class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass
    def do_POST(self):
        assert self.path == '/v1/chat/completions'
        calls.append(self.path)
        self.rfile.read(int(self.headers['Content-Length']))
        body = json.dumps({'model': 'mock-model', 'choices': [{'message': {'content': '连接成功'}}],
                           'usage': {'prompt_tokens': 60, 'completion_tokens': 24}}).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(body)

server = HTTPServer(('127.0.0.1', 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
root = TEST_ROOT
stage = root / ('account_page_test_' + uuid.uuid4().hex[:8])
shutil.copytree(GAME_DIR, stage, dirs_exist_ok=True,
                ignore=shutil.ignore_patterns('配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config', 'ai-settings.json'))
shutil.copy2(GAME_EXE, stage / 'EraAI.exe')
title = stage / 'ERB/SYSTEM_TITLE.ERB'
text = title.read_text(encoding='utf-8-sig').replace('@SYSTEM_TITLE\n', '@SYSTEM_TITLE\nCALL ACCOUNT_PAGE_TEST\nQUIT\n', 1)
title.write_text(text, encoding='utf-8-sig')
title = stage / 'ERB/SYSTEM_MODEL.ERB'
text = title.read_text(encoding='utf-8-sig')

a, b = text.index('@TITLE_SETTING_AI_MODEL_INFO\n'), text.index('\n@MODEL_SELECT_SOURCE\n')
page = text[a:b].replace('ESCAPE 0\nINPUT', 'ESCAPE 0\nRESULT = 0')
text = text[:a] + page + text[b:]
title.write_text(text, encoding='utf-8-sig')
script = '''@ACCOUNT_PAGE_TEST
CALL INIT_DISPLAY_SETTINGS
CALL APPLY_DISPLAY_SETTINGS
CALL TITLE_SETTING_AI_MODEL_INFO
CALL ASSERT_ACCOUNT_PAGE(0)
AICONNECTION 2
CALL TITLE_SETTING_AI_MODEL_INFO
CALL ASSERT_ACCOUNT_PAGE(1)
'''
for url, tutorial in [('https://api.deepseek.com', '前往 DeepSeek 开放平台'),
                      ('https://generativelanguage.googleapis.com/v1beta/openai', '前往 Google AI Studio'),
                      ('https://api.x.ai/v1', '前往 xAI 开发者平台'),
                      ('https://api.openai.com/v1', '前往 OpenAI API 平台')]:
    script += f'''RESULTS:0 '= "{url}"
AICONNECTION 3
CALL TITLE_SETTING_AI_MODEL_INFO
CALL ASSERT_ACCOUNT_PAGE(1)
LOCAL:2 = 0
FOR LOCAL:1, 0, LINECOUNT
    IF STRFIND(GETDISPLAYLINE(LOCAL:1), "{tutorial}") >= 0
        LOCAL:2 += 1
    ENDIF
NEXT
IF LOCAL:2 != 1
    THROW 平台教程未正确匹配
ENDIF
'''
script += f'''RESULTS:0 '= "http://127.0.0.1:{server.server_port}/v1"
AICONNECTION 3
RESULTS:0 '= "mock-model"
AICONNECTION 4
RESULTS:0 '= "fake-page-test-key"
AICONNECTION 5
AISTART "测试", "测试", "connection_test", "", 64, 0, "标准"
$ACCOUNT_CHAT_WAIT
TWAIT 50, 1
AIPOLL
IF RESULT == 0
    GOTO ACCOUNT_CHAT_WAIT
ENDIF
IF RESULT != 1
    THROW 模拟测试请求失败
ENDIF
CALL TITLE_SETTING_AI_MODEL_INFO
CALL ASSERT_ACCOUNT_PAGE(1)
AICONNECTION 16
IF STRFIND(RESULTS:2, "输入 60 / 输出 24") < 0
    THROW 页面未读取实际累计用量
ENDIF
IF STRFIND(RESULTS:1, "无法完整估算") < 0
    THROW 未知价格没有明确提示
ENDIF
AICONNECTION 18
IF RESULT != 1
    THROW 游戏内消耗图表未打开
ENDIF
AICONNECTION 19
IF RESULT != 0
    THROW 游戏内图表状态不正确
ENDIF
SAVETEXT "PASS account page", "account_pass.txt", 0, 1
QUIT

@ASSERT_ACCOUNT_PAGE(ARG)
LOCAL:0 = 0
LOCAL:2 = 0
FOR LOCAL:1, 0, LINECOUNT
    IF STRFIND(GETDISPLAYLINE(LOCAL:1), "账户余额：") >= 0
        LOCAL:0 += 1
    ENDIF
    IF STRFIND(GETDISPLAYLINE(LOCAL:1), "今日花费（本游戏，估算）：") >= 0
        LOCAL:0 += 1
    ENDIF
    IF STRFIND(GETDISPLAYLINE(LOCAL:1), "今日用量（本游戏）：") >= 0
        LOCAL:0 += 1
    ENDIF
    IF STRFIND(GETDISPLAYLINE(LOCAL:1), "查看近7日消耗折线图") >= 0
        LOCAL:2 += 1
    ENDIF
NEXT
IF LOCAL:0 != ARG * 3
    THROW 账户信息没有按接入方式显示或隐藏
ENDIF
IF LOCAL:2 != ARG
    THROW 消耗图表入口未按网络 API 接入方式显示
ENDIF
RETURN
'''
(stage / 'ERB/ACCOUNT_PAGE_TEST.ERB').write_text(script, encoding='utf-8-sig')
marker = stage / 'account_pass.txt'
if marker.exists():
    marker.unlink()
proc = subprocess.Popen([str((stage / 'EraAI.exe').resolve())])
try:
    deadline = time.monotonic() + 25
    while proc.poll() is None and not marker.exists() and time.monotonic() < deadline:
        time.sleep(.2)
    assert marker.exists(), 'Account page engine test did not finish'
    assert marker.read_text(encoding='utf-8-sig').strip() == 'PASS account page'
    assert calls == ['/v1/chat/completions']
finally:
    server.shutdown()
    if proc.poll() is None:
        proc.terminate()
print('PASS: API-only account rows, all provider tutorials and persisted mock usage; no real API requests.')
