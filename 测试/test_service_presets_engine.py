from test_support import TEST_ROOT, GAME_DIR, GAME_EXE
from pathlib import Path
import shutil,subprocess,threading,json,time,uuid
from http.server import HTTPServer,BaseHTTPRequestHandler
seen=[]
class Handler(BaseHTTPRequestHandler):
 def log_message(self,*args):pass
 def do_GET(self):
  assert self.path=='/v1/models'
  assert self.headers.get('Authorization')=='Bearer fake-catalog-engine-key'
  seen.append(self.path)
  body=json.dumps({'data':[{'id':'model-b','context_window':262144,'max_output_tokens':40000},{'id':'model-a'}]}).encode()
  self.send_response(200);self.send_header('Content-Type','application/json');self.end_headers();self.wfile.write(body)
server=HTTPServer(('127.0.0.1',0),Handler);threading.Thread(target=server.serve_forever,daemon=True).start()
r=TEST_ROOT;p=r/('catalog_preset_test_'+uuid.uuid4().hex[:8]);shutil.copytree(GAME_DIR,p,ignore=shutil.ignore_patterns('配置','数据','*.sav','*.log','profile','setting.json','emuera.config','ai-settings.json'));shutil.copy2(GAME_EXE,p/'EraAI.exe')
f=p/'ERB/SYSTEM_TITLE.ERB';s=f.read_text(encoding='utf-8-sig').replace('@SYSTEM_TITLE\n','@SYSTEM_TITLE\nCALL CATALOG_ENGINE_TEST\nQUIT\n',1);f.write_text(s,encoding='utf-8-sig')
(p/'ERB/CATALOG_ENGINE_TEST.ERB').write_text(f'''@CATALOG_ENGINE_TEST
CALL INIT_DISPLAY_SETTINGS
CALL APPLY_DISPLAY_SETTINGS
AICONNECTION 2
RESULTS:0 '= "http://127.0.0.1:{server.server_port}/v1"
AICONNECTION 3
RESULTS:0 '= "fake-catalog-engine-key"
AICONNECTION 5
AICONNECTION 8
IF RESULT < 0
    THROW 列表请求启动失败
ENDIF
$CATALOG_ENGINE_WAIT
TWAIT 50, 1
AICONNECTION 11
IF RESULT == 0
    GOTO CATALOG_ENGINE_WAIT
ENDIF
IF RESULT != 1
    THROW 列表请求失败
ENDIF
RESULT = 0
AICONNECTION 13
IF RESULT != 2
    THROW 列表数量不正确
ENDIF
RESULT = 2
AICONNECTION 12
IF RESULT != 1
    THROW 模型选择失败
ENDIF
AICONNECTION 0
IF RESULTS:0 != "model-b"
    THROW 模型不是服务返回的标识
ENDIF
AIGENERATION 0
IF STRFIND(RESULTS:10, "262144 Token") < 0 || STRFIND(RESULTS:2, "262144 Token") < 0
    THROW 未读取并应用服务提供的模型容量
ENDIF
RESULTS:0 '= "32768"
AIGENERATION 1
IF RESULT != 1
    THROW 错误沿用旧统一生成上限
ENDIF
RESULTS:0 '= "40001"
AIGENERATION 1
IF RESULT != -1
    THROW 未按模型最大输出拦截错误预算
ENDIF
AIGENERATION 0
IF STRFIND(RESULTS:1, "32768 Token") != 0
    THROW 错误预算覆盖原设置
ENDIF
SAVETEXT "PASS service model catalog selection", "catalog_pass.txt", 0, 1
RETURN
''',encoding='utf-8-sig')
marker=p/'catalog_pass.txt'
if marker.exists():marker.unlink()
proc=subprocess.Popen([str((p/'EraAI.exe').resolve())]);deadline=time.monotonic()+25
while proc.poll() is None and not marker.exists() and time.monotonic()<deadline:time.sleep(.2)
server.shutdown()
if proc.poll() is None:proc.terminate()
assert (p/'catalog_pass.txt').read_text(encoding='utf-8-sig').strip()=='PASS service model catalog selection'
assert seen==['/v1/models']
print('PASS：独立引擎从模拟服务读取列表并保存所选模型，全流程通过。')
