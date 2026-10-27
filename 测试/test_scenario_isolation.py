from test_support import structured_context, TEST_ROOT, GAME_DIR
from http.server import HTTPServer, BaseHTTPRequestHandler
import json, re, shutil, subprocess, threading, time

requests = []
class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_): pass
    def do_POST(self):
        request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        system = request['messages'][0]['content']
        requests.append(request)
        assert all(word not in system for word in ['rain_room', '艾拉', 'ella', '食材', '狩猎'])
        context = structured_context(request)
        assert context['owners'].get('captain') == '舰长'
        assert 'vault' not in context['owners'], 'unrelated storage must stay outside the turn context'
        assert [item['id'] for item in context['inventory']['items']] == [7]
        turn = context['turn_id']
        revision = int(context['base_revision'])
        reply = dict(story='舰长收好了晶体。', turn_id=turn, base_revision=revision,
                     inventory_ops=[dict(op='change_quantity', item_id=7, owner='captain', delta=2, reason='维修奖励')],
                     suggestions=['检查反应堆'])
        body = json.dumps(dict(choices=[dict(message=dict(content=json.dumps(reply)), finish_reason='stop')])).encode()
        self.send_response(200); self.send_header('Content-Type', 'application/json')
        self.end_headers(); self.wfile.write(body)

server = HTTPServer(('127.0.0.1', 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
try:
    for case in ['alongside', 'standalone', 'empty']:
        stage = (TEST_ROOT / ('scenario-isolation-' + case)).resolve()
        shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns('配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
        scripts = stage / 'ERB/script'
        if case != 'alongside':
            assert scripts.resolve().is_relative_to(TEST_ROOT.resolve())
            shutil.rmtree(scripts)
            scripts.mkdir()
        (stage / '配置').mkdir()
        (stage / '配置/music-settings.json').write_text(json.dumps(dict(Enabled=False, Volume=50)), encoding='utf-8')
        title = stage / 'ERB/SYSTEM_TITLE.ERB'
        title.write_text(title.read_text(encoding='utf-8-sig').replace('@SYSTEM_TITLE\n', '@SYSTEM_TITLE\nCALL ISOLATION_TEST\nQUIT\n', 1), encoding='utf-8-sig')
        main = stage / 'ERB/SYSTEM_MAIN.ERB'
        main.write_text(main.read_text(encoding='utf-8-sig').replace('ESCAPE 0\nINPUT', 'ESCAPE 0\nRESULT = GLOBAL:880', 1), encoding='utf-8-sig')
        test = '''@ISOLATION_TEST
CALL INIT_DISPLAY_SETTINGS
CALL INIT_NEWGAME
'''
        if case == 'empty':
            test += '''RESULT = 0
AICONNECTION 46
IF RESULT != 0
    THROW empty scenario catalog
ENDIF
GLOBAL:880 = 0
CALL SCENARIO_SELECT
CALL LOAD_SCENARIO_SELECT
SAVETEXT "PASS isolation", "isolation-pass.txt", 0, 1
QUIT
'''
        else:
            package = scripts / 'station'
            package.mkdir()
            manifest = dict(format_version=1, id='orbital', name='轨道站', author='fixture', type='隔离验证', entry='ORBIT_START',
                            resume='ORBIT_RESUME', init='ORBIT_INIT', before_save='ORBIT_BEFORE_SAVE', after_load='ORBIT_AFTER_LOAD',
                            turn_end='ORBIT_TURN_END', save_caption='ORBIT_CAPTION', owners=dict(captain='舰长', vault='仓库'),
                            reply_label='值班记录', default_suggestions=['检查反应堆'])
            (package / '剧本.json').write_text(json.dumps(manifest, ensure_ascii=False), encoding='utf-8')
            (package / '流程.ERB').write_text('''@ORBIT_INIT
FLAG:70 = 0
FLAG:71 = 0
FLAG:72 = 0
AICONNECTION 30
SAVESTR:61 '= RESULTS:0
RESULTS:1 '= "2"
RESULTS:2 '= "导航图"
RESULTS:3 '= "轨道站导航资料"
RESULTS:4 '= "普通物品"
RESULTS:5 '= ""
RESULTS:6 '= "1"
RESULTS:7 '= "vault"
RESULTS:8 '= ""
AICONNECTION 31
IF RESULT < 0
    THROW second scenario seed map
ENDIF
RESULTS:1 '= "7"
RESULTS:2 '= "晶体"
RESULTS:3 '= "能源晶体"
RESULTS:4 '= "消耗品"
RESULTS:5 '= ""
RESULTS:6 '= "5"
RESULTS:7 '= "captain"
RESULTS:8 '= ""
AICONNECTION 31
IF RESULT < 0
    THROW second scenario seed crystal
ENDIF
SAVESTR:61 '= RESULTS:0
RETURN 1

@ORBIT_START
IF SAVESTR:0 != "orbital"
    THROW framework did not select second scenario
ENDIF
CALLEVENT EVENTTURNEND
IF FLAG:70 != 1 || TIME != 12
    THROW second scenario turn hook not called
ENDIF
GLOBAL:300 = 1
CALL GAME_AUTOSAVE
IF CHKDATA(99) != 0 || STRFIND(RESULTS:0, "[scenario:orbital]") != 0
    THROW generic autosave did not tag scenario id
ENDIF
CALL CHECK_SCENARIO_SAVE, 99, "orbital"
IF RESULT != 1
    THROW second scenario save not found
ENDIF
FLAG:71 = 99
SAVESTR:61 '= ""
LOADDATA 99
THROW generic resume failed

@ORBIT_BEFORE_SAVE
FLAG:71 += 1
RETURN 1
@ORBIT_CAPTION
RESULTS:0 '= "轨道站 维修进度"
RETURN 1
@ORBIT_TURN_END
FLAG:70 += 1
TIME += 12
RETURN 1
@ORBIT_AFTER_LOAD
FLAG:72 += 1
RETURN 1

@ORBIT_RESUME
IF SAVESTR:0 != "orbital" || TIME != 12 || FLAG:71 != 1 || FLAG:72 != 1
    THROW different scenario save or lifecycle not restored
ENDIF
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= "2"
RESULTS:4 '= "vault"
AICONNECTION 32
IF RESULT < 0 || RESULTS:1 != "1"
    THROW item two implicitly treated as food
ENDIF
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= "检查晶体"
AICONNECTION 34
IF RESULT < 0
    THROW other scenario inventory prepare failed
ENDIF
AISTART "轨道站维修记录", "检查晶体", "wrong_id", "shift 1", 1024, 0, ""
IF RESULT != -1
    THROW prepared state attached to another scenario
ENDIF
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= "检查晶体"
AICONNECTION 34
AISTART "轨道站维修记录", "检查晶体", "orbital", "shift 1", 1024, 0, ""
IF RESULT != 0
    THROW second scenario cannot start model request
ENDIF
$ORBIT_POLL
TWAIT 100, 1
AIPOLL
IF RESULT == 0
    GOTO ORBIT_POLL
ENDIF
IF RESULT != 1 || RESULTS:5 != "1"
    THROW second scenario did not use validated inventory reply
ENDIF
LOCALS:0 '= RESULTS:0
LOCALS:1 '= RESULTS:2
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= LOCALS:1
AICONNECTION 35
IF RESULT < 0
    THROW second scenario cannot commit
ENDIF
SAVESTR:61 '= RESULTS:0
RESULTS:1 '= "7"
RESULTS:4 '= "captain"
AICONNECTION 32
IF RESULT < 0 || RESULTS:1 != "7"
    THROW second scenario quantity incorrect
ENDIF
RESULTS:1 '= "orbital"
RESULTS:2 '= "shift 1"
RESULTS:3 '= "检查晶体"
RESULTS:4 '= LOCALS:0
AICONNECTION 22
AIMEMORY "orbital"
IF STRFIND(RESULTS:0, "值班记录：") < 0 || STRFIND(RESULTS:0, "艾拉") >= 0
    THROW another scenario memory label leaked
ENDIF
SAVETEXT "PASS isolation", "isolation-pass.txt", 0, 1
QUIT
''', encoding='utf-8-sig')
            if case == 'alongside':
                test += '''RESULTS:0 '= "rain_room"
AICONNECTION 47
SAVEDATA 99, "[scenario:rain_room] existing save"
'''
            test += f'''AICONNECTION 2
RESULTS:0 '= "http://127.0.0.1:{server.server_port}/v1"
AICONNECTION 3
RESULTS:0 '= "mock-isolation"
AICONNECTION 4
RESULTS:0 '= "fake-isolation-key"
AICONNECTION 5
RESULT = 0
AICONNECTION 46
LOCAL:80 = RESULT
GLOBAL:880 = 0
FOR LOCAL:81, 0, LOCAL:80
    IF RESULTS:(20 + LOCAL:81) == "orbital"
        GLOBAL:880 = LOCAL:81 + 1
    ENDIF
NEXT
IF GLOBAL:880 == 0
    THROW second scenario not discovered
ENDIF
CALL SCENARIO_SELECT
THROW second scenario did not execute entry
'''
        (stage / 'ERB/ISOLATION_TEST.ERB').write_text(test, encoding='utf-8-sig')
        proc = subprocess.Popen([str(stage / 'EraAI.exe')])
        try:
            marker = stage / 'isolation-pass.txt'; deadline = time.monotonic() + 25
            while proc.poll() is None and not marker.exists() and time.monotonic() < deadline: time.sleep(.15)
            if not marker.exists():
                for log in stage.glob('*.log'): print(log.read_text(encoding='utf-8-sig', errors='replace')[-7000:])
            assert marker.exists(), case + ': scenario isolation engine failed'
            if case != 'empty':
                assert (stage / '数据/存档/orbital/save99.sav').exists()
            if case == 'alongside':
                prior = (stage / '数据/存档/rain_room/save99.sav').read_bytes()
                assert b'existing save' in prior, 'different scenario autosave was overwritten'
            print('PASS scenario isolation engine: ' + case, flush=True)
        finally:
            if proc.poll() is None: proc.terminate()
            proc.wait(timeout=10)
    assert len(requests) == 2, 'both installed and standalone scenarios must use their own inventory protocol'
finally:
    server.shutdown()
