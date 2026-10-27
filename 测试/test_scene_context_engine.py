import sys
sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')

from test_support import structured_context, TEST_ROOT, GAME_DIR
from http.server import HTTPServer, BaseHTTPRequestHandler
import json
import shutil
import subprocess
import threading
import time


# All requests go to this loopback mock. No personal settings, credentials or saves are copied.
requests = []
handler_errors = []


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        try:
            request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            requests.append(request)
            context = structured_context(request)
            index = len(requests)
            assert index <= 3, 'unexpected extra scene request'
            if index == 1:
                assert context['scene']['location'] == '森林边的小屋'
                assert set(context['scene']['present_characters']) == {'player', 'ella'}
                content = dict(
                    story='你独自走到溪边，穿过灌木时左臂被划出一处浅伤口。艾拉仍留在家中。',
                    inventory_ops=[],
                    state_ops=[dict(op='create_status', character='player', name='左臂划伤', category='伤势',
                                    severity='轻微', description='左臂有一处尚未处理的浅划伤。',
                                    effects='抬臂时有些疼痛。', reason='本轮穿过溪边灌木时划伤')],
                    scene_update=dict(location='溪边', present_characters=['player'],
                                      summary='主角独自在溪边，左臂刚被灌木划伤；艾拉留在小屋。',
                                      reason='主角离开小屋，独自走到溪边'),
                )
            elif index == 2:
                assert context['scene']['location'] == '屋外不远的溪边'
                assert context['scene']['present_characters'] == ['player']
                assert context['inventory']['scope'] == 'relevant'
                assert context['inventory']['complete'] is False
                assert context['inventory']['items'] == [], 'unrelated weather chat resent inventory'
                assert any(s['character'] == 'player' and s['name'] == '左臂划伤'
                           and '尚未处理' in s['description'] for s in context['statuses']['items'])
                content = dict(story='溪水在石间流过。你留在溪边观察天色，左臂的浅伤口仍有些疼痛。',
                               inventory_ops=[], state_ops=[])
            else:
                assert context['scene']['location'] == '屋外不远的溪边'
                content = dict(story='你离开溪边，独自走到了村口。', inventory_ops=[], state_ops=[],
                               scene_update=dict(location='村口', present_characters=['player'],
                                                 summary='主角独自在村口观察道路。', reason='本轮离开溪边，步行到村口'))
            content.update(turn_id=context['turn_id'], base_revision=int(context['base_revision']), suggestions=[], effort='none')
            body = json.dumps(dict(choices=[dict(message=dict(content=json.dumps(content)), finish_reason='stop')])).encode()
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(body)
        except Exception as error:
            handler_errors.append(repr(error))
            self.send_response(500)
            self.end_headers()


def replace_once(path, old, new):
    text = path.read_text(encoding='utf-8-sig')
    assert text.count(old) == 1, f'scene test hook is missing or ambiguous: {path.name}'
    path.write_text(text.replace(old, new, 1), encoding='utf-8-sig')


stage = TEST_ROOT / 'scene-context-lifecycle'
shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns(
    '配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
(stage / '配置').mkdir()
(stage / '配置/music-settings.json').write_text(json.dumps(dict(Enabled=False, Volume=50)), encoding='utf-8')
server = HTTPServer(('127.0.0.1', 0), Handler)
thread = threading.Thread(target=server.serve_forever, daemon=True)
thread.start()
proc = None

try:
    folder = stage / 'ERB/script/艾拉的奇幻冒险'
    replace_once(stage / 'ERB/SYSTEM_TITLE.ERB', '@SYSTEM_TITLE\n', '@SYSTEM_TITLE\nCALL SCENE_CONTEXT_TEST\nQUIT\n')
    replace_once(folder / '物品确认.ERB', '$RAIN_ROOM_REVIEW_ITEMS_INPUT\nESCAPE 0\nINPUT',
                 '$RAIN_ROOM_REVIEW_ITEMS_INPUT\nESCAPE 0\nCALL SCENE_CONTEXT_REVIEW')
    replace_once(folder / '流程.ERB', '$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nINPUT',
                 '$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nRESULT = 0')
    # Unexpected model failures must return to the driver's assertions instead of waiting for input.
    flow = folder / '流程.ERB'
    flow.write_text(flow.read_text(encoding='utf-8-sig').replace('\n    WAIT\n', '\n    ; scene test: return after failure\n'),
                    encoding='utf-8-sig')
    replace_once(stage / 'ERB/SYSTEM_SCENARIO_LOAD.ERB', 'CALL SCENARIO_CALL_HOOK, 3\nBEGIN TITLE',
                 'CALL SCENE_CONTEXT_AFTER_LOAD\nQUIT')

    driver = f'''@SCENE_CONTEXT_TEST
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL INIT_DISPLAY_SETTINGS
CALL RAIN_ROOM_ENSURE_FOOD
SAVETEXT SAVESTR:61, "scene-initial.txt", 0, 1
RESULTS:0 '= SAVESTR:61
AICONNECTION 55
IF RESULT < 0 || RESULTS:0 != "森林边的小屋" || DAY != 1 || TIME != 0
    THROW explicit new game did not initialize its opening scene
ENDIF
AICONNECTION 2
RESULTS:0 '= "http://127.0.0.1:{server.server_port}/v1"
AICONNECTION 3
RESULTS:0 '= "mock-scene-context"
AICONNECTION 4
RESULTS:0 '= "fake-scene-test-key"
AICONNECTION 5
CALL NEW_RAIN_ROOM_AI_HISTORY
CALL RAIN_ROOM_REQUEST_ACTION, "我独自走到溪边，穿过灌木时划伤了左臂。"
IF TIME != 1 || GLOBAL:510 != 1 || GLOBAL:801 != 1 || STRFIND(SAVESTR:61, "左臂划伤") < 0
    THROW scene and injury proposal not committed together
ENDIF
RESULTS:0 '= SAVESTR:61
AICONNECTION 55
IF RESULT < 0 || RESULTS:0 != "屋外不远的溪边"
    THROW accepted proposal did not update the authoritative scene
ENDIF
SAVETEXT SAVESTR:61, "scene-adopted.txt", 0, 1
CALL RAIN_ROOM_REQUEST_ACTION, "我在溪边看看天气，左臂还有点疼。"
IF TIME != 2 || GLOBAL:510 != 2 || GLOBAL:801 != 1 || STRFIND(SAVESTR:61, "尚未处理") < 0
    THROW ordinary conversation lost injury or unexpectedly required another review
ENDIF
SAVESTR:70 '= SAVESTR:61
CALL CAPTURE_RAIN_ROOM_AI_HISTORY
SAVEDATA 0, "scene context snapshot"
SAVETEXT SAVESTR:61, "scene-saved.txt", 0, 1
CALL RAIN_ROOM_REQUEST_ACTION, "我离开溪边，走到村口看看。"
IF SAVESTR:61 != SAVESTR:70 || TIME != 2 || GLOBAL:510 != 2 || GLOBAL:801 != 2
    THROW rejected scene proposal altered scene time or history
ENDIF
SAVETEXT SAVESTR:61, "scene-cancelled.txt", 0, 1
CALL RAIN_ROOM_SHORT_REST
IF DAY != 1 || TIME != 3 || FLAG:20 != 10 || GLOBAL:510 != 3 || STRFIND(SAVESTR:61, "尚未处理") < 0
    THROW short rest changed injury or incorrectly settled a day
ENDIF
RESULTS:0 '= SAVESTR:61
AICONNECTION 55
IF RESULT < 0 || RESULTS:0 != "屋外不远的溪边"
    THROW short rest moved the player away from the current scene
ENDIF
SAVETEXT SAVESTR:61, "scene-rested.txt", 0, 1
LOADDATA 0
THROW scene load event not entered

@SCENE_CONTEXT_REVIEW
GLOBAL:801 += 1
RESULTS:0 '= SAVESTR:61
AICONNECTION 55
IF GLOBAL:801 == 1
    IF RESULTS:0 != "森林边的小屋" || TIME != 0 || GLOBAL:510 != 0
        THROW scene was committed before confirmation
    ENDIF
    SAVETEXT SAVESTR:61, "scene-before-confirm.txt", 0, 1
    RETURN 1
ELSEIF GLOBAL:801 == 2
    IF RESULTS:0 != "屋外不远的溪边" || TIME != 2 || GLOBAL:510 != 2
        THROW pending scene proposal altered live progress before rejection
    ENDIF
    RETURN 0
ENDIF
THROW unexpected scene confirmation

@SCENE_CONTEXT_AFTER_LOAD
IF DAY != 1 || TIME != 2 || FLAG:20 != 10 || GLOBAL:510 != 2 || SAVESTR:61 != SAVESTR:70
    THROW save load failed to restore the earlier scene and progress
ENDIF
RESULTS:0 '= SAVESTR:61
AICONNECTION 55
IF RESULT < 0 || RESULTS:0 != "屋外不远的溪边" || STRFIND(SAVESTR:61, "尚未处理") < 0
    THROW load hook replaced the saved scene or removed the injury
ENDIF
SAVETEXT SAVESTR:61, "scene-restored.txt", 0, 1
RESULTS:0 '= "rain_room"
AICONNECTION 20
SAVETEXT RESULTS:0, "scene-restored-memory.txt", 0, 1
SAVETEXT "PASS scene context", "scene-pass.txt", 0, 1
QUIT
'''
    (stage / 'ERB/SCENE_CONTEXT_TEST.ERB').write_text(driver, encoding='utf-8-sig')
    proc = subprocess.Popen([str(stage / 'EraAI.exe')])
    marker = stage / 'scene-pass.txt'
    deadline = time.monotonic() + 40
    while proc.poll() is None and not marker.exists() and time.monotonic() < deadline:
        time.sleep(.15)
    if not marker.exists():
        for log in stage.glob('*.log'):
            print(log.read_text(encoding='utf-8-sig', errors='replace')[-7000:])
        if handler_errors:
            print('Scene mock assertions:', handler_errors)
    assert marker.exists(), 'scene context engine lifecycle did not complete'
    assert not handler_errors, handler_errors
    assert len(requests) == 3, 'local rest or loading unexpectedly requested the model'

    def read(name):
        return json.loads((stage / name).read_text(encoding='utf-8-sig'))

    initial = read('scene-initial.txt')
    adopted = read('scene-adopted.txt')
    saved = read('scene-saved.txt')
    rested = read('scene-rested.txt')
    assert read('scene-before-confirm.txt') == initial, 'proposal changed state before adoption'
    assert initial['scene']['location'] == '森林边的小屋'
    assert set(initial['scene']['present_characters']) == {'player', 'ella'}
    assert adopted['scene']['location'] == '屋外不远的溪边' and adopted['scene']['present_characters'] == ['player']
    assert saved['scene'] == adopted['scene'] and saved['statuses'] == adopted['statuses']
    assert read('scene-cancelled.txt') == saved
    assert rested['scene']['location'] == '屋外不远的溪边'
    assert rested['scene']['present_characters'] == ['player']
    assert rested['character_locations']['ella'] == '森林边的小屋'
    assert rested['statuses'] == adopted['statuses'], 'rest implicitly healed the condition'
    assert read('scene-restored.txt') == saved, 'actual save/load did not restore scene snapshot exactly'
    assert (stage / '数据/存档/rain_room/save00.sav').is_file(), 'no real save file was written'
    memory = read('scene-restored-memory.txt')
    assert len(memory) == 2
    assert all('走到了村口' not in turn['Reply'] and '回家休息' not in turn['Player'] for turn in memory)
    print('PASS scene context engine: new game, adoption, partial context, rejection, rest and real save/load', flush=True)
finally:
    if proc is not None:
        if proc.poll() is None:
            proc.terminate()
        proc.wait(timeout=10)
    server.shutdown()
    server.server_close()
    thread.join(timeout=2)
