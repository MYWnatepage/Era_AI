import sys
sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
from test_support import structured_context, TEST_ROOT, GAME_DIR
from http.server import HTTPServer, BaseHTTPRequestHandler
import json, re, shutil, subprocess, threading, time

for case, roll, gain in [('success', 20, 8), ('http_failure', 9, 3), ('invalid_ops', 1, 0), ('cancel', 15, 5), ('random', 0, 0)]:
    requests = []
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_): pass
        def do_POST(self):
            request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            requests.append(request)
            if case == 'http_failure':
                self.send_response(500); self.end_headers(); return
            if case == 'cancel': time.sleep(.3)
            system = request['messages'][0]['content']
            context = structured_context(request)
            turn = context['turn_id']
            revision = int(context['base_revision'])
            if len(requests) == 1:
                action = context.get('program_action', {})
                assert action and 'instruction' in action
                if case != 'random': assert action['roll'] == roll
                else: assert 1 <= action['roll'] <= 20
            ops = [] if case != 'invalid_ops' else [{'op': 'change_quantity', 'item_id': 2, 'owner': 'player', 'delta': 99, 'reason': '违规额外收益'}]
            content = {'story': '你完成今天的狩猎，整理好所得后仍留在这片林地。' if len(requests) == 1 else '你在小屋中向艾拉讲起今天的狩猎。',
                       'turn_id': turn, 'base_revision': revision, 'inventory_ops': ops, 'effort': 'none',
                       'suggestions': ['和艾拉聊聊今天的见闻', '整理物资', '回家休息'] if len(requests) == 1 else ['和艾拉聊聊']}
            if len(requests) == 2:
                content['scene_update'] = dict(location='森林边的小屋', present_characters=['player', 'ella'],
                    character_locations=dict(player='森林边的小屋', ella='森林边的小屋'), summary='两人在小屋中交谈。', reason='两人在当前地点交谈。')
            body = json.dumps({'choices': [{'message': {'content': json.dumps(content)}, 'finish_reason': 'stop'}]}).encode()
            try:
                self.send_response(200); self.send_header('Content-Type', 'application/json')
                self.end_headers(); self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError): pass

    stage = TEST_ROOT / ('actions-' + case)
    shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns('配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
    (stage / '配置').mkdir()
    (stage / '配置/music-settings.json').write_text(json.dumps({'Enabled': False, 'Volume': 50}), encoding='utf-8')
    server = HTTPServer(('127.0.0.1', 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
    title = stage / 'ERB/SYSTEM_TITLE.ERB'
    title.write_text(title.read_text(encoding='utf-8-sig').replace('@SYSTEM_TITLE\n', '@SYSTEM_TITLE\nCALL ACTIONS_TEST\nQUIT\n', 1), encoding='utf-8-sig')
    folder = stage / 'ERB/script/艾拉的奇幻冒险'
    definition = folder / '剧本.json'
    scenario = json.loads(definition.read_text(encoding='utf-8-sig'))
    scenario['initial_scene'] = dict(location='狩猎林地', present_characters=['player'], summary='主角独自在允许狩猎的林地。')
    definition.write_text(json.dumps(scenario, ensure_ascii=False), encoding='utf-8')
    flow = folder / '流程.ERB'
    text = flow.read_text(encoding='utf-8-sig')
    text = text.replace('$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nINPUT', '$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nRESULT = 0', 1)
    if case == 'cancel': text = text.replace('PRINTL 稍候片刻……', 'AICANCEL\nPRINTL 稍候片刻……', 1)
    flow.write_text(text, encoding='utf-8-sig')
    actions = folder / '常用行动.ERB'
    actions.write_text(actions.read_text(encoding='utf-8-sig').replace('    WAIT\n    RETURN', '    RETURN'), encoding='utf-8-sig')
    review = folder / '物品确认.ERB'
    review.write_text(review.read_text(encoding='utf-8-sig').replace('$RAIN_ROOM_REVIEW_ITEMS_INPUT\nESCAPE 0\nINPUT', '$RAIN_ROOM_REVIEW_ITEMS_INPUT\nESCAPE 0\nRESULT = 1'), encoding='utf-8-sig')
    inventory = folder / '当前物品.ERB'
    inventory.write_text(inventory.read_text(encoding='utf-8-sig').replace('ESCAPE 0\nINPUT', 'ESCAPE 0\nRESULT = 0', 1), encoding='utf-8-sig')
    loader = stage / 'ERB/SYSTEM_SCENARIO_LOAD.ERB'
    loader.write_text(loader.read_text(encoding='utf-8-sig').replace('CALL SCENARIO_CALL_HOOK, 3\nBEGIN TITLE', 'CALL ACTIONS_AFTER_LOAD\nQUIT'), encoding='utf-8-sig')
    common = f'''@ACTIONS_TEST
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL INIT_DISPLAY_SETTINGS
CALL RAIN_ROOM_ENSURE_FOOD
; Convert legacy 4-period time once without charging food.
DAY = 4
TIME = 3
FLAG:24 = 0
CALL RAIN_ROOM_SYNC_TIME
IF DAY != 4 || TIME != 6 || FLAG:20 != 10
    THROW legacy time migration
ENDIF
CALL RAIN_ROOM_SYNC_TIME
IF TIME != 6
    THROW time migrated twice
ENDIF
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL RAIN_ROOM_ENSURE_FOOD
FLAG:0 = 99
FLAG:1 = 77
FLAG:2 = 1
GLOBAL:300 = 1
GLOBAL:301 = 0
SAVESTR:0 '= "rain_room"
AICONNECTION 2
RESULTS:0 '= "http://127.0.0.1:{server.server_port}/v1"
AICONNECTION 3
RESULTS:0 '= "mock-actions"
AICONNECTION 4
RESULTS:0 '= "fake-actions-key"
AICONNECTION 5
CALL NEW_RAIN_ROOM_AI_HISTORY
FLAG:30 = {roll}
FLAG:33 = {0 if case == "random" else 1}
'''
    if case == 'success':
        common += '''CALL RAIN_ROOM_HUNT
IF FLAG:20 != 18 || TIME != 4 || FLAG:32 != 1
    THROW hunt did not consume half day or award eight food
ENDIF
CALL RAIN_ROOM_HUNT
IF FLAG:20 != 18 || TIME != 4 || GLOBAL:510 != 1
    THROW repeated hunt altered state
ENDIF
; Settle the return through the program movement bridge before ordinary dialogue.
; Native drawn-route travel is covered separately; AI cannot move the player.
FOR LOCAL:90, 0, 2
    RESULTS:0 '= SAVESTR:61
    RESULTS:1 '= STRFORM("{2 - LOCAL:90}")
    RESULTS:2 '= STRFORM("{DAY}")
    RESULTS:3 '= STRFORM("{TIME}")
    AICONNECTION 105
    IF RESULT < 0
        THROW program return route failed
    ENDIF
    SAVESTR:61 '= RESULTS:0
    LOCAL:91 = TOINT(RESULTS:2)
    CALL RAIN_ROOM_ADVANCE_TIME, LOCAL:91
NEXT
IF TIME != 6
    THROW program return route cost incorrect
ENDIF
CALL RAIN_ROOM_RUN_INPUT, "我和艾拉谈谈今天的狩猎。"
IF TIME != 7 || FLAG:20 != 18
    THROW ordinary dialogue did not cost one tick
ENDIF
RESULTS:0 '= SAVESTR:61
AICONNECTION 55
IF RESULTS:0 != "森林边的小屋"
    THROW confirmed return home was not saved
ENDIF
CALL RAIN_ROOM_SLEEP
IF DAY != 2 || TIME != 0 || FLAG:20 != 16 || GLOBAL:510 != 3
    THROW sleep did not settle day exactly once
ENDIF
CALL RAIN_ROOM_INVENTORY_PAGE
IF DAY != 2 || TIME != 0 || FLAG:20 != 16 || FLAG:0 != 99 || FLAG:1 != 77
    THROW readonly inventory advanced time or obsolete counters changed
ENDIF
SAVESTR:69 '= SAVESTR:61
CALL CAPTURE_RAIN_ROOM_AI_HISTORY
SAVEDATA 0, "actions snapshot"
DAY = 99
FLAG:32 = 99
SAVESTR:61 '= ""
LOADDATA 0
THROW load did not finish

@ACTIONS_AFTER_LOAD
CALL RAIN_ROOM_ENSURE_FOOD
IF DAY != 2 || TIME != 0 || FLAG:20 != 16 || FLAG:32 != 1 || SAVESTR:69 != SAVESTR:61
    THROW action state or inventory not restored
ENDIF
IF GLOBAL:510 != 3
    THROW action history not restored
ENDIF
RESULTS:0 '= "rain_room"
AICONNECTION 20
SAVETEXT RESULTS:0, "actions-memory-result.txt", 0, 1
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL RAIN_ROOM_ENSURE_FOOD
IF FLAG:30 != 0 || FLAG:32 != 0 || FLAG:33 != 0 || FLAG:20 != 10
    THROW new game did not reset hunt state
ENDIF
SAVETEXT "PASS actions", "actions-pass.txt", 0, 1
QUIT
'''
    else:
        expected_food = '10 + FLAG:31' if case == 'random' else str(10 + gain)
        expected_after_rest = '8 + FLAG:31' if case == 'random' else str(8 + gain)
        roll_check = 'FLAG:30 < 1 || FLAG:30 > 20' if case == 'random' else f'FLAG:30 != {roll}'
        common += f'''CALL RAIN_ROOM_HUNT
IF {roll_check} || FLAG:20 != {expected_food} || TIME != 4 || FLAG:32 != 1 || GLOBAL:510 != 1
    THROW fallback rerolled, duplicated or missed controlled hunt outcome
ENDIF
CALL RAIN_ROOM_HUNT
IF FLAG:20 != {expected_food} || TIME != 4 || GLOBAL:510 != 1
    THROW completed hunt ran twice
ENDIF
CALL RAIN_ROOM_SHORT_REST
IF DAY != 1 || TIME != 5 || FLAG:20 != {expected_food} || GLOBAL:510 != 2
    THROW short rest moved time or food incorrectly
ENDIF
CALL RAIN_ROOM_ADVANCE_TIME, 3
IF DAY != 2 || TIME != 0 || FLAG:20 != {expected_after_rest}
    THROW day rollover or daily food cost wrong
ENDIF
TIME = 5
CALL RAIN_ROOM_HUNT
IF DAY != 2 || TIME != 5 || FLAG:20 != {expected_after_rest} || FLAG:33 != 1
    THROW insufficient time still rolled or consumed resources
ENDIF
IF FLAG:0 != 99 || FLAG:1 != 77
    THROW obsolete affection and fatigue changed
ENDIF
SAVETEXT "PASS actions", "actions-pass.txt", 0, 1
QUIT

@ACTIONS_CHOICE
THROW unexpected menu
@ACTIONS_AFTER_LOAD
THROW unexpected load
'''
    (stage / 'ERB/ACTIONS_TEST.ERB').write_text(common, encoding='utf-8-sig')
    proc = subprocess.Popen([str(stage / 'EraAI.exe')])
    try:
        marker = stage / 'actions-pass.txt'; deadline = time.monotonic() + 30
        while proc.poll() is None and not marker.exists() and time.monotonic() < deadline: time.sleep(.15)
        if not marker.exists():
            for log in stage.glob('*.log'): print(log.read_text(encoding='utf-8-sig', errors='replace')[-6500:])
        assert marker.exists(), case + ': engine did not complete actions test'
        history_file = 'actions-memory-result.txt' if case == 'success' else '数据/记忆/rain_room.json'
        history = json.loads((stage / history_file).read_text(encoding='utf-8-sig'))
        if case == 'success':
            assert json.loads((stage / '数据/记忆/rain_room.json').read_text(encoding='utf-8-sig')) == []
        assert len(history) == (3 if case == 'success' else 2)
        assert all('inventory_ops' not in turn['Reply'] for turn in history)
        assert len(requests) == 2 if case == 'success' else len(requests) <= 1
        print('PASS actions engine: ' + case, flush=True)
    finally:
        if proc.poll() is None: proc.terminate()
        proc.wait(timeout=10)
        server.shutdown(); thread.join(timeout=2)
