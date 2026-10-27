import sys
sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
from test_support import structured_context, TEST_ROOT, GAME_DIR
from http.server import HTTPServer, BaseHTTPRequestHandler
import json, re, shutil, subprocess, threading, time

for case in ['accept', 'cancel', 'inspect_cancel', 'no_change', 'bad_suggestions', 'omitted_items']:
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_): pass
        def do_POST(self):
            request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            system = request['messages'][0]['content']
            context = structured_context(request)
            turn = context['turn_id']
            revision = int(context['base_revision'])
            ops = [] if case in ['no_change', 'bad_suggestions', 'omitted_items'] else [{'op': 'change_quantity', 'item_id': 3, 'owner': 'player', 'delta': 100, 'reason': '模型提出的赠送，需玩家核对来源'}]
            content = {'story': '本轮剧情。', 'turn_id': turn, 'base_revision': revision, 'inventory_ops': ops, 'effort': 'none',
                       'suggestions': ['一', '二', '三', '四'] if case == 'bad_suggestions' else []}
            body = json.dumps({'choices': [{'message': {'content': json.dumps(content)}, 'finish_reason': 'stop'}]}).encode()
            self.send_response(200); self.end_headers(); self.wfile.write(body)
    stage = TEST_ROOT / ('inventory-review-' + case)
    shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns('配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
    (stage / '配置').mkdir()
    (stage / '配置/music-settings.json').write_text(json.dumps({'Enabled': False, 'Volume': 50}), encoding='utf-8')
    server = HTTPServer(('127.0.0.1', 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
    folder = stage / 'ERB/script/艾拉的奇幻冒险'
    title = stage / 'ERB/SYSTEM_TITLE.ERB'
    title.write_text(title.read_text(encoding='utf-8-sig').replace('@SYSTEM_TITLE\n', '@SYSTEM_TITLE\nCALL REVIEW_TEST\nQUIT\n', 1), encoding='utf-8-sig')
    flow = folder / '流程.ERB'
    flow.write_text(flow.read_text(encoding='utf-8-sig').replace('$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nINPUT', '$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nRESULT = 0', 1), encoding='utf-8-sig')
    review = folder / '物品确认.ERB'
    review.write_text(review.read_text(encoding='utf-8-sig').replace('$RAIN_ROOM_REVIEW_ITEMS_INPUT\nESCAPE 0\nINPUT', '$RAIN_ROOM_REVIEW_ITEMS_INPUT\nSAVETEXT LOCALS:3, \"review-warning.txt\", 0, 1\nESCAPE 0\nCALL REVIEW_CHOICE'), encoding='utf-8-sig')
    inventory = folder / '当前物品.ERB'
    inventory.write_text(inventory.read_text(encoding='utf-8-sig').replace('ESCAPE 0\nINPUT', 'ESCAPE 0\nRESULT = 0', 1), encoding='utf-8-sig')
    rejected = case in ['cancel', 'inspect_cancel', 'omitted_items']
    expected_gold = 102 if case == 'accept' else 2
    expected_count = 0 if rejected else 1
    expected_reviews = 2 if case == 'inspect_cancel' else 1 if case in ['accept', 'cancel', 'omitted_items'] else 0
    (stage / 'ERB/REVIEW_TEST.ERB').write_text(f'''@REVIEW_TEST
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL INIT_DISPLAY_SETTINGS
CALL RAIN_ROOM_ENSURE_FOOD
SAVESTR:69 '= SAVESTR:61
AICONNECTION 2
RESULTS:0 '= "http://127.0.0.1:{server.server_port}/v1"
AICONNECTION 3
RESULTS:0 '= "mock-review"
AICONNECTION 4
RESULTS:0 '= "fake-review-key"
AICONNECTION 5
CALL NEW_RAIN_ROOM_AI_HISTORY
CALL RAIN_ROOM_RUN_INPUT, "{'我找到两条布条。' if case == 'omitted_items' else '今天好吗'}"
IF DAY != 1 || TIME != {expected_count} || GLOBAL:510 != {expected_count} || GLOBAL:801 != {expected_reviews}
    THROW review flow advanced or recorded wrong state
ENDIF
IF {1 if rejected else 0} == 1 && SAVESTR:61 != SAVESTR:69
    THROW cancelled proposal modified inventory snapshot
ENDIF
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= "3"
AICONNECTION 37
IF RESULTS:4 != "主角：{expected_gold}"
    THROW inventory changed before acceptance or failed to commit
ENDIF
IF {1 if case == 'bad_suggestions' else 0} == 1 && STRFIND(SAVESTR:18, "建议") < 0
    THROW invalid suggestions lost their explanation
ENDIF
SAVETEXT "PASS review", "review-pass.txt", 0, 1
QUIT

@REVIEW_CHOICE
GLOBAL:801 += 1
IF GLOBAL:801 == 1 && {1 if case == 'inspect_cancel' else 0} == 1
    RETURN 2
ENDIF
RETURN {1 if case == 'accept' else 0}
''', encoding='utf-8-sig')
    proc = subprocess.Popen([str(stage / 'EraAI.exe')])
    try:
        marker = stage / 'review-pass.txt'; deadline = time.monotonic() + 25
        while proc.poll() is None and not marker.exists() and time.monotonic() < deadline: time.sleep(.15)
        if not marker.exists():
            for log in stage.glob('*.log'): print(log.read_text(encoding='utf-8-sig', errors='replace')[-6000:])
        assert marker.exists(), case + ': item review did not finish'
        memory = json.loads((stage / '数据/记忆/rain_room.json').read_text(encoding='utf-8-sig'))
        assert len(memory) == expected_count
        assert all('inventory_ops' not in turn['Reply'] for turn in memory)
        if case == 'omitted_items':
            assert '物品核对' in (stage / 'review-warning.txt').read_text(encoding='utf-8-sig')
        print('PASS item review: ' + case, flush=True)
    finally:
        if proc.poll() is None: proc.terminate()
        proc.wait(timeout=10); server.shutdown(); thread.join(timeout=2)
