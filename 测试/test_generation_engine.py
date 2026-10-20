from test_support import TEST_ROOT, GAME_DIR, GAME_EXE
from pathlib import Path
from http.server import HTTPServer, BaseHTTPRequestHandler
import json
import shutil
import subprocess
import threading
import time
import uuid

requests = []
class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_): pass
    def do_GET(self):
        assert self.path == '/v1/models'
        body = json.dumps({'data': [{'id': 'mock-model', 'context_window': 65536, 'max_output_tokens': 8192}]}).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(body)
    def do_POST(self):
        assert self.path == '/v1/chat/completions'
        data = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        requests.append(data)
        body = json.dumps({'model': 'mock-model', 'choices': [{'message': {'content': '测试回复'}}],
                           'usage': {'prompt_tokens': 60, 'completion_tokens': 24}}).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(body)

server = HTTPServer(('127.0.0.1', 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
root = TEST_ROOT
stage = root / ('generation_engine_' + uuid.uuid4().hex[:8])
shutil.copytree(GAME_DIR, stage, dirs_exist_ok=True,
                ignore=shutil.ignore_patterns('配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config', 'ai-settings.json'))
shutil.copy2(GAME_EXE, stage / 'EraAI.exe')
title = stage / 'ERB/SYSTEM_TITLE.ERB'
text = title.read_text(encoding='utf-8-sig').replace('@SYSTEM_TITLE\n', '@SYSTEM_TITLE\nCALL GENERATION_ENGINE_TEST\nQUIT\n', 1)
a, b = text.index('@TITLE_SETTING\n'), text.index('\n@INIT_DISPLAY_SETTINGS\n')
text = text[:a] + text[a:b].replace('\nINPUT\n', '\nRESULT = 0\n', 1) + text[b:]
title.write_text(text, encoding='utf-8-sig')
menus = stage / 'ERB/SYSTEM_GENERATION.ERB'
text = menus.read_text(encoding='utf-8-sig')
names = ['GENERATION_SAMPLING_PAGE', 'GENERATION_LENGTH_PAGE', 'GENERATION_CONTEXT_PAGE', 'GENERATION_THINKING_PAGE']
for i, name in enumerate(names):
    a = text.index('@' + name)
    b = text.find('\n@', a + 1)
    if b < 0: b = len(text)
    segment = text[a:b]
    slot = 950 + i
    choices = [[3, 4, 5, 6, 7, 8], [6, 5], [4, 2, 3], [3]][i]
    feed = f'GLOBAL:{slot} += 1\nSELECTCASE GLOBAL:{slot}\n'
    for n, choice in enumerate(choices, 1):
        feed += f'    CASE {n}\n        RESULT = {choice}\n'
    feed += '    CASEELSE\n        RESULT = 0\nENDSELECT'
    segment = segment.replace('\nINPUT\n', '\n' + feed + '\n', 1)
    if i == 1:
        segment = segment.replace('        INPUTS', '''        IF GLOBAL:960 == 1
            RESULTS:0 '= ""
        ELSE
            RESULTS:0 '= "1536"
        ENDIF''')
    if i == 2:
        segment = segment.replace('        INPUTS', '        RESULTS:0 \'= "4096"', 1)
        segment = segment.replace('        INPUTS', '        RESULTS:0 \'= "2"', 1)
    if i == 0:
        feed_text = 'SELECTCASE GLOBAL:950\n'
        for n, value in enumerate(['0.6', '0.9', '0.2', '0.1'], 3):
            feed_text += f'    CASE {n}\n        RESULTS:0 \'= "{value}"\n'
        feed_text += 'ENDSELECT'
        segment = segment.replace('        INPUTS\n', '\n' + feed_text + '\n')
    text = text[:a] + segment + text[b:]
menus.write_text(text, encoding='utf-8-sig')
memory = stage / '数据/记忆'
memory.mkdir(parents=True)
(memory / 'rain_room.json').write_text(json.dumps([{'Time': f'day{i}', 'Player': f'user{i}', 'Reply': f'reply{i}'} for i in range(3)]), encoding='utf-8')
script = f'''@GENERATION_ENGINE_TEST
CALL INIT_DISPLAY_SETTINGS
CALL APPLY_DISPLAY_SETTINGS
AICONNECTION 2
RESULTS:0 '= "http://127.0.0.1:{server.server_port}/v1"
AICONNECTION 3
RESULTS:0 '= "mock-model"
AICONNECTION 4
RESULTS:0 '= "fake-generation-engine-key"
AICONNECTION 5
CLEARLINE LINECOUNT
CALL SETTING_SUMMARY, "Summary", "字段甲：ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZ / 字段乙：ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZ / 字段丙：ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZ / 字段丁：ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZ / 字段戊：ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZ"
IF LINECOUNT < 3
    THROW 长摘要未按窗口宽度分行
ENDIF
FOR LOCAL:1, 0, LINECOUNT
    LOCALS:0 '= GETDISPLAYLINE(LOCAL:1)
    IF HTML_STRINGLEN(HTML_ESCAPE(LOCALS:0), 1) > CLIENTWIDTH()
        THROW 分行后的摘要超过窗口宽度
    ENDIF
NEXT
CALL TITLE_SETTING
LOCAL:0 = 0
FOR LOCAL:1, 0, LINECOUNT
    LOCALS:0 '= GETDISPLAYLINE(LOCAL:1)
    IF STRFIND(LOCALS:0, "[10] 采样设置") >= 0 || STRFIND(LOCALS:0, "[11] 回复长度") >= 0 || STRFIND(LOCALS:0, "[12] 上下文管理") >= 0 || STRFIND(LOCALS:0, "[13] 思考模式") >= 0
        LOCAL:0 += 1
    ENDIF
    IF STRFIND(LOCALS:0, "回复上限") >= 0 || STRFIND(LOCALS:0, "记忆容量") >= 0 || STRFIND(LOCALS:0, "对话风格") >= 0
        THROW 旧参数入口未移除
    ENDIF
NEXT
IF LOCAL:0 != 4
    THROW 新参数入口未完整显示
ENDIF
CALL GENERATION_SAMPLING_PAGE
CALL GENERATION_LENGTH_PAGE
CALL GENERATION_CONTEXT_PAGE
CALL GENERATION_THINKING_PAGE
AIGENERATION 0
IF STRFIND(RESULTS:1, "1536 Token") != 0 || STRFIND(RESULTS:2, "4096 Token") != 0 || RESULTS:3 != "2" || RESULTS:4 != "0.6" || RESULTS:5 != "0.9" || RESULTS:6 != "0.2" || RESULTS:7 != "0.1" || RESULTS:8 != "低"
    THROW 参数页面未实际保存所选数值
ENDIF
GLOBAL:951 = 0
GLOBAL:960 = 1
CALL GENERATION_LENGTH_PAGE
AIGENERATION 0
IF STRFIND(RESULTS:1, "1536 Token") != 0
    THROW 取消输入改变了生成预算
ENDIF
AISTART "mock request", "current user", "rain_room", "day4", 64, 0, ""
$GENERATION_ENGINE_WAIT
TWAIT 50, 1
AIPOLL
IF RESULT == 0
    GOTO GENERATION_ENGINE_WAIT
ENDIF
IF RESULT != 1
    THROW 游戏生成请求失败
ENDIF
SAVETEXT "PASS generation engine", "generation_pass.txt", 0, 1
QUIT
'''
(stage / 'ERB/GENERATION_ENGINE_TEST.ERB').write_text(script, encoding='utf-8-sig')
marker = stage / 'generation_pass.txt'
process = subprocess.Popen([str((stage / 'EraAI.exe').resolve())])
try:
    deadline = time.monotonic() + 25
    while process.poll() is None and not marker.exists() and time.monotonic() < deadline:
        time.sleep(.2)
    assert marker.exists(), 'Generation menu engine test did not finish'
    assert len(requests) == 1
    data = requests[0]
    assert data['max_tokens'] == 1536 and data['temperature'] == .6 and data['top_p'] == .9
    assert data['frequency_penalty'] == .2 and data['presence_penalty'] == .1
    assert len(data['messages']) == 6 and data['messages'][1]['content'] == 'user1'
    assert 'reasoning_effort' not in data and 'thinking' not in data
finally:
    server.shutdown()
    if process.poll() is None: process.terminate()
print('PASS: merged sampling menu and other parameter menus, saved values, cancellation and actual native request parameters.')
