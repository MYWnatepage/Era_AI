from test_support import structured_context, TEST_ROOT, PROJECT_ROOT, GAME_DIR
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
import json, re, shutil, subprocess, threading, time

for case in ['success', 'http_failure', 'cancel', 'timeout']:
    requests = []
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_): pass
        def do_POST(self):
            request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            requests.append(request)
            time.sleep(11 if case == 'timeout' else 3 if case == 'cancel' else 1.8)
            if case == 'http_failure':
                self.send_response(500); self.end_headers(); return
            system = request['messages'][0]['content']
            context = structured_context(request)
            assert context['program_action']['roll'] == 15
            content = dict(story='你完成今天的狩猎，整理好所得，仍留在这片林地。',
                           turn_id=context['turn_id'],
                           base_revision=int(context['base_revision']),
                           inventory_ops=[])
            body = json.dumps(dict(choices=[dict(message=dict(content=json.dumps(content)), finish_reason='stop')])).encode()
            try:
                self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers(); self.wfile.write(body)
            except (BrokenPipeError, ConnectionError, OSError): pass
    server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    server.daemon_threads = True
    thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
    stage = TEST_ROOT / ('dice-' + case)
    shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns('配置','数据','*.sav','*.log','profile','setting.json','emuera.config'))
    (stage / '配置').mkdir()
    (stage / '配置/music-settings.json').write_text(json.dumps(dict(Enabled=False,Volume=50)),encoding='utf-8')
    definition = stage / 'ERB/script/艾拉的奇幻冒险/剧本.json'
    scenario = json.loads(definition.read_text(encoding='utf-8-sig'))
    scenario['initial_scene'] = dict(location='狩猎林地', present_characters=['player'], summary='主角独自在允许狩猎的林地。')
    definition.write_text(json.dumps(scenario, ensure_ascii=False), encoding='utf-8')
    title = stage / 'ERB/SYSTEM_TITLE.ERB'
    title.write_text(title.read_text(encoding='utf-8-sig').replace('@SYSTEM_TITLE\n','@SYSTEM_TITLE\nCALL DICE_WAIT_TEST\nQUIT\n',1),encoding='utf-8-sig')
    flow = stage / 'ERB/script/艾拉的奇幻冒险/流程.ERB'
    flow.write_text(flow.read_text(encoding='utf-8-sig').replace('$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nINPUT','$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nRESULT = 0',1),encoding='utf-8-sig')
    fixture = f'''@DICE_WAIT_TEST
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL INIT_DISPLAY_SETTINGS
CALL RAIN_ROOM_ENSURE_FOOD
GLOBAL:300 = 0
SAVESTR:0 '= "rain_room"
AICONNECTION 2
RESULTS:0 '= "http://127.0.0.1:{server.server_port}/v1"
AICONNECTION 3
RESULTS:0 '= "dice-test"
AICONNECTION 4
RESULTS:0 '= "fake-dice-key"
AICONNECTION 5
RESULTS:0 '= "10"
AICONNECTION 6
CALL NEW_RAIN_ROOM_AI_HISTORY
FLAG:30 = 15
FLAG:33 = DAY
CALL RAIN_ROOM_HUNT
IF FLAG:30 != 15 || FLAG:20 != 15 || TIME != 4 || FLAG:32 != 1 || GLOBAL:510 != 1
    THROW animation changed original dice or committed more than once
ENDIF
SAVETEXT "PASS dice", "dice-pass.txt", 0, 1
QUIT
'''
    (stage / 'ERB/DICE_WAIT_TEST.ERB').write_text(fixture,encoding='utf-8-sig')
    assembly = PROJECT_ROOT / '源码/Emuera/artifacts/bin/Emuera/release-naudio_win-x64/EraAI.dll'
    command = ['dotnet','run','--no-build','--project',str(PROJECT_ROOT / '测试/NativeAI.Tests.csproj'),'-c','Release','--','--dice-engine-ui',str(assembly),str(stage)]
    if case == 'cancel': command += ['--cancel-dice']
    try:
        subprocess.run(command,check=True,timeout=40)
        assert (stage / 'dice-pass.txt').exists(), case
        assert len(requests) == 1, 'animation must never issue extra model requests'
        assert len(json.loads((stage / '数据/记忆/rain_room.json').read_text(encoding='utf-8-sig'))) == 1
        print('PASS real dice lifecycle: ' + case,flush=True)
    finally:
        server.shutdown(); server.server_close(); thread.join(timeout=2)
