import sys
sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
from test_support import TEST_ROOT, GAME_DIR
from http.server import HTTPServer, BaseHTTPRequestHandler
import json, shutil, subprocess, threading, time

requests = []
class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_): pass
    def do_POST(self):
        assert self.path == '/v1/chat/completions'
        requests.append(json.loads(self.rfile.read(int(self.headers['Content-Length']))))
        current = json.loads(requests[-1]['messages'][-1]['content'])
        context = current['program_context']
        turn_id = context['turn_id']
        revision = context['base_revision']
        structured = json.dumps({'story': '我在这里。', 'turn_id': turn_id, 'base_revision': revision,
                                 'inventory_ops': [], 'state_ops': [], 'effort': 'none',
                                 'scene_update': {'location': '森林边的小屋', 'present_characters': ['player', 'ella'],
                                                  'summary': '两人在小屋里开始交谈。', 'reason': '玩家向艾拉问好。'}})
        body = json.dumps({'choices': [{'message': {'content': structured}}],
                           'usage': {'prompt_tokens': 30, 'completion_tokens': 8}}).encode()
        self.send_response(200); self.send_header('Content-Type', 'application/json')
        self.end_headers(); self.wfile.write(body)

stage = TEST_ROOT / 'rain-room-cards'
shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns('配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
folder = stage / 'ERB/script/艾拉的奇幻冒险'
assert all((folder / name).is_file() for name in ['人物卡.ERB', '物品卡.ERB', '剧本接口.ERB', '流程.ERB', '历史同步.ERB'])
assert not (stage / 'ERB/SYSTEM_AI_HISTORY.ERB').exists()
assert not (stage / 'ERB/script/SCENARIO_TEST_RAIN_ROOM_NO_AI.ERB').exists()
# A change to the cards must reach the actual request, rather than leaving duplicate prompt text in the flow.
p = folder / '人物卡.ERB'
character_text = p.read_text(encoding='utf-8-sig').replace('C_BRIEF \'= "', 'C_BRIEF \'= "PERSONA_CARD_MARKER：', 1)
character_text = '\n'.join('C_BACKGROUND \'= "PRIVATE_DETAIL_MARKER"' if line.startswith('C_BACKGROUND \'= ') else line for line in character_text.splitlines()) + '\n'
p.write_text(character_text, encoding='utf-8-sig')
p = folder / '剧本.json'
manifest = json.loads(p.read_text(encoding='utf-8-sig'))
manifest['content']['prompt_core'] = 'SCENARIO_CARD_MARKER：资料标记："炉火"\\木屋。' + manifest['content']['prompt_core']
p.write_text(json.dumps(manifest, ensure_ascii=False), encoding='utf-8')
p = folder / '流程.ERB'
text = p.read_text(encoding='utf-8-sig')
# Exercise the request flow directly; input-composer navigation has its own regression test.
text = text.replace('$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nINPUT', '''$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT
ESCAPE 0
RESULTS:0 '= SAVESTR:61
AICONNECTION 55
IF RESULT < 0 || RESULTS:0 != "森林边的小屋" || RESULTS:1 != "两人在小屋里开始交谈。"
    THROW 本轮场景摘要未保存
ENDIF
SAVETEXT "PASS cards", "cards-pass.txt", 0, 1
QUIT''', 1)
p.write_text(text, encoding='utf-8-sig')
(stage / '配置').mkdir()
(stage / '配置/music-settings.json').write_text(json.dumps({'Enabled': False, 'Volume': 50, 'CustomPath': ''}), encoding='utf-8')
server = HTTPServer(('127.0.0.1', 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
title = stage / 'ERB/SYSTEM_TITLE.ERB'
title.write_text(title.read_text(encoding='utf-8-sig').replace('@SYSTEM_TITLE\n', '@SYSTEM_TITLE\nCALL CARDS_TEST\nQUIT\n', 1), encoding='utf-8-sig')
(stage / 'ERB/CARDS_TEST.ERB').write_text(f'''@CARDS_TEST
CALL INIT_DISPLAY_SETTINGS
CALL APPLY_DISPLAY_SETTINGS
DAY = 3
TIME = 2
FLAG:0 = 7
FLAG:1 = 4
GLOBAL:510 = 0
CALL RAIN_ROOM_CHARACTER_CARD
IF RESULTS:0 != "艾拉"
    THROW 人物卡没有加载
ENDIF
CALL RAIN_ROOM_INITIAL_CHARACTER, 0
IF RESULT != 2
    THROW 初始人物数不正确
ENDIF
CALL RAIN_ROOM_INITIAL_CHARACTER, 2
IF RESULT != 1 || RESULTS:3 != "player" || STRFIND(RESULTS:4, "11岁") < 0 || STRFIND(RESULTS:5, "树脂") < 0
    THROW 主角卡未正确读取
ENDIF
CALL RAIN_ROOM_SCENARIO_CARD
IF RESULTS:0 != "rain_room" || RESULTS:1 != "艾拉的奇幻冒险" || RESULTS:3 != "测试"
    THROW 剧本卡没有加载
ENDIF
IF RESULTS:17 != "100" || RESULTS:18 != "100"
    THROW 货币汇率未正确读取
ENDIF
CALL RAIN_ROOM_INITIAL_ITEM, 0
IF RESULT != 11
    THROW 初始物品总数不正确
ENDIF
CALL RAIN_ROOM_INITIAL_ITEM, 1
IF RESULT != 1 || RESULTS:0 != "1" || RESULTS:1 != "铜钥匙" || RESULTS:3 != "普通物品" || RESULTS:5 != "2" || RESULTS:6 != "player"
    THROW 初始物品1与作者设定不一致
ENDIF
CALL RAIN_ROOM_INITIAL_ITEM, 2
IF RESULT != 1 || RESULTS:0 != "2" || RESULTS:1 != "食材" || RESULTS:3 != "消耗品" || RESULTS:5 != "10" || RESULTS:6 != "player"
    THROW 初始物品2与作者设定不一致
ENDIF
CALL RAIN_ROOM_INITIAL_ITEM, 3
IF RESULT != 1 || RESULTS:0 != "3" || RESULTS:1 != "金币" || RESULTS:3 != "普通物品" || RESULTS:5 != "2" || RESULTS:6 != "player"
    THROW 初始物品3与作者设定不一致
ENDIF
CALL RAIN_ROOM_INITIAL_ITEM, 4
IF RESULT != 1 || RESULTS:0 != "4" || RESULTS:1 != "银币" || RESULTS:3 != "普通物品" || RESULTS:5 != "36" || RESULTS:6 != "player"
    THROW 初始物品4与作者设定不一致
ENDIF
CALL RAIN_ROOM_INITIAL_ITEM, 5
IF RESULT != 1 || RESULTS:0 != "5" || RESULTS:1 != "铜币" || RESULTS:3 != "普通物品" || RESULTS:5 != "81" || RESULTS:6 != "player"
    THROW 初始物品5与作者设定不一致
ENDIF
CALL RAIN_ROOM_INITIAL_ITEM, 6
IF RESULT != 1 || RESULTS:0 != "6" || RESULTS:1 != "左轮手枪" || RESULTS:3 != "普通物品" || RESULTS:5 != "1" || RESULTS:6 != "player"
    THROW 初始物品6与作者设定不一致
ENDIF
CALL RAIN_ROOM_INITIAL_ITEM, 7
IF RESULT != 1 || RESULTS:0 != "7" || RESULTS:1 != "子弹" || RESULTS:3 != "消耗品" || RESULTS:5 != "5" || RESULTS:6 != "player"
    THROW 初始物品7与作者设定不一致
ENDIF
CALL RAIN_ROOM_INITIAL_ITEM, 8
IF RESULT != 1 || RESULTS:0 != "8" || RESULTS:1 != "高碳钢长矛" || RESULTS:3 != "普通物品" || RESULTS:5 != "1" || RESULTS:6 != "player"
    THROW 初始物品8与作者设定不一致
ENDIF
CALL RAIN_ROOM_INITIAL_ITEM, 9
IF RESULT != 1 || RESULTS:0 != "9" || RESULTS:1 != "复合弓" || RESULTS:3 != "普通物品" || RESULTS:5 != "1" || RESULTS:6 != "player"
    THROW 初始物品9与作者设定不一致
ENDIF
CALL RAIN_ROOM_INITIAL_ITEM, 10
IF RESULT != 1 || RESULTS:0 != "10" || RESULTS:1 != "弓箭" || RESULTS:3 != "消耗品" || RESULTS:5 != "3" || RESULTS:6 != "player"
    THROW 初始物品10与作者设定不一致
ENDIF
CALL RAIN_ROOM_INITIAL_ITEM, 11
IF RESULT != 1 || RESULTS:0 != "11" || RESULTS:1 != "毽子" || RESULTS:3 != "普通物品" || RESULTS:5 != "1" || RESULTS:6 != "ella"
    THROW 初始物品11与作者设定不一致
ENDIF
RESULTS:0 '= "STALE"
CALL RAIN_ROOM_INITIAL_ITEM, 12
IF RESULT != 0 || RESULTS:0 != ""
    THROW 不存在的物品未拒绝或残留上一条记录
ENDIF
CALL RAIN_ROOM_ITEM_CARDS
IF RESULT != 0 || RESULTS:0 != ""
    THROW 对话入口不应自动发送整张初始表
ENDIF
IF DAY != 3 || TIME != 2 || FLAG:0 != 7 || FLAG:1 != 4
    THROW 读取卡片重置了游戏状态
ENDIF
AICONNECTION 2
RESULTS:0 '= "http://127.0.0.1:{server.server_port}/v1"
AICONNECTION 3
RESULTS:0 '= "mock-model"
AICONNECTION 4
RESULTS:0 '= "fake-cards-test-key"
AICONNECTION 5
CALL NEW_RAIN_ROOM_AI_HISTORY
CALL RAIN_ROOM_ENSURE_FOOD
RESULTS:0 '= SAVESTR:61
AICONNECTION 55
IF RESULTS:0 != "未记录（以最近剧情为准）"
    THROW 旧档库存迁移不应推测初始场景
ENDIF
RESULTS:0 '= SAVESTR:61
AICONNECTION 57
IF RESULT < 0
    THROW 新游戏场景初始化失败
ENDIF
SAVESTR:61 '= RESULTS:0
CALL RAIN_ROOM_BUILD_PROMPT, "去集市买食材"
SAVETEXT RESULTS:0, "trade-material.txt", 0, 1
CALL RAIN_ROOM_RUN_INPUT, "你好，艾拉"
QUIT
''', encoding='utf-8-sig')
p = subprocess.Popen([str((stage / 'EraAI.exe').resolve())])
try:
    marker = stage / 'cards-pass.txt'
    deadline = time.monotonic() + 30
    while p.poll() is None and not marker.exists() and time.monotonic() < deadline: time.sleep(.15)
    if not marker.exists():
        for log in stage.glob('*.log'): print(log.read_text(encoding='utf-8-sig', errors='replace')[-5000:])
    assert marker.exists(), 'Card integration did not finish'
    assert len(requests) == 1
    trade_material = json.loads((stage / 'trade-material.txt').read_text(encoding='utf-8-sig'))
    assert '1金币＝100银币' in trade_material['relevant_rules'], trade_material
    assert '1银币＝100铜币' in trade_material['relevant_rules'], trade_material
    prompt = requests[0]['messages'][-1]['content']
    current = json.loads(prompt)
    assert current['player_input'] == '你好，艾拉', current
    assert isinstance(current['program_context']['scenario_material'], dict), current
    material_text = '\n'.join(str(value) for value in current['program_context']['scenario_material'].values())
    assert '资料标记："炉火"\\木屋。' in material_text, current
    assert prompt.count('你好，艾拉') == 1, prompt
    assert all(s in prompt for s in ['PERSONA_CARD_MARKER', 'SCENARIO_CARD_MARKER', '中世纪', '猎人', '7岁', '家人', '红发', '雀斑', '11岁', '黑发', '现代树脂', '开朗', '沉稳', '亲生父母', '不能擅自叫爸爸或妈妈']), prompt
    assert all(s not in prompt for s in ['PRIVATE_DETAIL_MARKER', '好感度', '疲劳']), prompt
    assert all(s not in prompt for s in ['又一个雨夜', '1金币＝', '1银币＝', '金币', '铜钥匙', '毽子']), prompt
    system = requests[0]['messages'][0]['content']
    assert all(s not in system for s in ['只使用简体中文', '你正在为中文文字冒险游戏扮演角色', '最终角色回复', '覆盖前面的纯角色回复']), system
finally:
    server.shutdown()
    if p.poll() is None: p.terminate(); p.wait(timeout=5)
print('PASS: nested scenario folder, structured card-backed request, no full inventory/opening/currency replay, 11 initial items, ownership, exchange rates and state preservation.')
