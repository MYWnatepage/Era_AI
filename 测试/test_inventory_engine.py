import sys
sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
from test_support import structured_context, TEST_ROOT, GAME_DIR
from http.server import HTTPServer, BaseHTTPRequestHandler
import json, re, shutil, subprocess, threading, time

requests = []
class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_): pass
    def do_POST(self):
        request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        requests.append(request)
        system = request['messages'][0]['content']
        context = structured_context(request)
        turn = context['turn_id']
        revision = int(context['base_revision'])
        reply = {
            'story': '你带回三份食材和一块月光石。\n艾拉收好你递来的铜钥匙。',
            'turn_id': turn, 'base_revision': revision, 'effort': 'none',
            'inventory_ops': [
                {'op': 'change_quantity', 'item_id': 2, 'owner': 'player', 'delta': 3, 'reason': '狩猎归来'},
                {'op': 'transfer_item', 'item_id': 1, 'from_owner': 'player', 'to_owner': 'ella', 'quantity': 1, 'reason': '交给艾拉'},
                {'op': 'create_item', 'name': '月光石', 'description': '泛着淡蓝光的石头。', 'type': '特殊物品', 'attributes': '用途未知', 'quantity': 1, 'owner': 'player', 'reason': '林中拾得'},
            ],
        }
        body = json.dumps({'choices': [{'message': {'content': json.dumps(reply)}, 'finish_reason': 'stop'}]}).encode()
        self.send_response(200); self.send_header('Content-Type', 'application/json')
        self.end_headers(); self.wfile.write(body)

stage = TEST_ROOT / 'inventory-engine'
shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns('配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
(stage / '配置').mkdir()
(stage / '配置/music-settings.json').write_text(json.dumps({'Enabled': False, 'Volume': 50}), encoding='utf-8')
server = HTTPServer(('127.0.0.1', 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
title = stage / 'ERB/SYSTEM_TITLE.ERB'
title.write_text(title.read_text(encoding='utf-8-sig').replace('@SYSTEM_TITLE\n', '@SYSTEM_TITLE\nCALL INVENTORY_ENGINE_TEST\nQUIT\n', 1), encoding='utf-8-sig')
folder = stage / 'ERB/script/艾拉的奇幻冒险'
review = folder / '物品确认.ERB'
review.write_text(review.read_text(encoding='utf-8-sig').replace('$RAIN_ROOM_REVIEW_ITEMS_INPUT\nESCAPE 0\nINPUT', '$RAIN_ROOM_REVIEW_ITEMS_INPUT\nESCAPE 0\nRESULT = 1'), encoding='utf-8-sig')
flow = folder / '流程.ERB'
flow.write_text(flow.read_text(encoding='utf-8-sig').replace('$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nINPUT', '$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nRESULT = 0', 1), encoding='utf-8-sig')
# Exercise the real compact item matrix, detail page and return path.
view = folder / '当前物品.ERB'
view.write_text(view.read_text(encoding='utf-8-sig').replace('ESCAPE 0\nINPUT', '''ESCAPE 0
GLOBAL:800 += 1
IF GLOBAL:800 == 1
    RESULT = 10012
ELSE
    RESULT = 0
ENDIF''', 1).replace('ESCAPE 0\nINPUT', '''ESCAPE 0
IF LOCALS:0 != "月光石" || LOCALS:1 != "泛着淡蓝光的石头。" || LOCALS:4 != "主角：1"
    THROW item detail did not show new record
ENDIF
RESULT = 0''', 1), encoding='utf-8-sig')
loader = stage / 'ERB/SYSTEM_SCENARIO_LOAD.ERB'
loader.write_text(loader.read_text(encoding='utf-8-sig').replace('CALL SCENARIO_CALL_HOOK, 3\nBEGIN TITLE', 'CALL INVENTORY_ENGINE_AFTER_LOAD\nQUIT'), encoding='utf-8-sig')
(stage / 'ERB/INVENTORY_ENGINE_TEST.ERB').write_text(f'''@INVENTORY_ENGINE_TEST
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL INIT_DISPLAY_SETTINGS
CALL RAIN_ROOM_ENSURE_INVENTORY
IF FLAG:20 != 10 || MONEY != 0
    THROW initial inventory or obsolete money balance
ENDIF
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= "2"
RESULTS:4 '= "player,ella"
AICONNECTION 32
IF RESULTS:2 != "11"
    THROW initial item count
ENDIF
AICONNECTION 2
RESULTS:0 '= "http://127.0.0.1:{server.server_port}/v1"
AICONNECTION 3
RESULTS:0 '= "mock-inventory-model"
AICONNECTION 4
RESULTS:0 '= "fake-inventory-test-key"
AICONNECTION 5
CALL NEW_RAIN_ROOM_AI_HISTORY
SAVESTR:0 '= "rain_room"
FLAG:2 = 1
TIME = 7
CALL RAIN_ROOM_RUN_INPUT, "购买食材，把一把铜钥匙交给艾拉"
IF DAY != 2 || TIME != 0 || FLAG:20 != 11 || GLOBAL:510 != 1
    THROW response did not update inventory and settle day once
ENDIF
IF STRFIND(SAVESTR:19, "inventory_ops") >= 0 || STRFIND(SAVESTR:25, "inventory_ops") >= 0
    THROW software protocol leaked into dialogue
ENDIF
CALL RAIN_ROOM_INVENTORY_PAGE
IF GLOBAL:800 != 2
    THROW inventory navigation did not finish
ENDIF
SAVESTR:63 '= SAVESTR:61
CALL CAPTURE_RAIN_ROOM_AI_HISTORY
SAVEDATA 0, "inventory roundtrip"
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= "9"
RESULTS:2 '= "2"
RESULTS:3 '= "player,ella"
AICONNECTION 36
SAVESTR:61 '= RESULTS:0
DAY = 99
LOADDATA 0
THROW load did not enter restore event

@INVENTORY_ENGINE_AFTER_LOAD
IF SAVESTR:61 != SAVESTR:63 || DAY != 2
    THROW inventory snapshot not restored
ENDIF
CALL RAIN_ROOM_ENSURE_INVENTORY
IF FLAG:20 != 11
    THROW restored food was reset
ENDIF
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= "12"
AICONNECTION 37
IF RESULTS:0 != "月光石" || RESULTS:1 != "泛着淡蓝光的石头。"
    THROW new item description missing after save/load
ENDIF
SAVETEXT SAVESTR:61, "inventory-result.txt", 0, 1
RESULTS:0 '= "rain_room"
AICONNECTION 20
SAVETEXT RESULTS:0, "inventory-memory-result.txt", 0, 1
; Migration from the previous food-only version must retain remaining food.
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
; A food-only legacy save has no inventory/scene snapshot; new-game initialization now creates one.
SAVESTR:61 '= ""
FLAG:21 = 1
FLAG:20 = 4
FLAG:22 = 3
DAY = 4
CALL RAIN_ROOM_ENSURE_INVENTORY
IF FLAG:20 != 4 || FLAG:22 != 3
    THROW migration reset previously consumed food
ENDIF
RESULTS:0 '= SAVESTR:61
AICONNECTION 55
IF RESULTS:0 != "未记录（以最近剧情为准）"
    THROW legacy migration assumed opening location
ENDIF
CALL RAIN_ROOM_SETTLE_FOOD
IF FLAG:20 != 2
    THROW migrated inventory daily deduction
ENDIF
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL RAIN_ROOM_ENSURE_INVENTORY
IF FLAG:20 != 10 || STRFIND(SAVESTR:61, "月光石") >= 0
    THROW new game retained previous generated items
ENDIF
SAVETEXT "PASS inventory", "inventory-pass.txt", 0, 1
QUIT
''', encoding='utf-8-sig')
proc = subprocess.Popen([str(stage / 'EraAI.exe')])
try:
    marker = stage / 'inventory-pass.txt'
    deadline = time.monotonic() + 30
    while proc.poll() is None and not marker.exists() and time.monotonic() < deadline: time.sleep(.15)
    if not marker.exists():
        for log in stage.glob('*.log'): print(log.read_text(encoding='utf-8-sig', errors='replace')[-6000:])
    assert marker.exists(), 'inventory engine integration did not finish'
    assert len(requests) == 1
    snapshot = json.loads((stage / 'inventory-result.txt').read_text(encoding='utf-8-sig'))
    items = {item['id']: item for item in snapshot['items']}
    assert snapshot['next_id'] == 13 and items[2]['quantities']['player'] == 11
    assert items[1]['quantities'] == {'player': 1, 'ella': 1}
    assert items[12]['name'] == '月光石' and items[12]['description'] == '泛着淡蓝光的石头。'
    memory = json.loads((stage / 'inventory-memory-result.txt').read_text(encoding='utf-8-sig'))
    assert json.loads((stage / '数据/记忆/rain_room.json').read_text(encoding='utf-8-sig')) == []
    assert len(memory) == 1 and 'inventory_ops' not in memory[0]['Reply']
    print('PASS: real AI exchange, atomic inventory commit, food settlement, paged item view, snapshot save/load, migration, fresh game')
finally:
    server.shutdown()
    if proc.poll() is None: proc.terminate()
    proc.wait(timeout=10)
