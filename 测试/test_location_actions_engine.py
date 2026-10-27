"""Fixed actions, legacy movement bridge, rest and food regression; map travel has its own UI test."""
import sys
sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
from test_support import TEST_ROOT, GAME_DIR
from pathlib import Path
import json, re, shutil, subprocess, time

stage = TEST_ROOT / 'location-actions'
shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns(
    '配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
(stage / '配置').mkdir()
(stage / '配置/music-settings.json').write_text(json.dumps(dict(Enabled=False, Volume=50)), encoding='utf-8')
folder = stage / 'ERB/script/艾拉的奇幻冒险'
scenario = json.loads((folder / '剧本.json').read_text(encoding='utf-8-sig'))
scenario['fatigue_rules']['enabled'] = False
(folder / '剧本.json').write_text(json.dumps(scenario, ensure_ascii=False, indent=2), encoding='utf-8')
rest_rule, sleep_rule = scenario['recovery_rules']['rest'], scenario['recovery_rules']['sleep']
severity, severity_level = max(rest_rule['severity_levels'].items(), key=lambda entry: entry[1])
fatigue_name, fatigue_category = next(iter(rest_rule.get('status_names', [])), '跋涉疲劳'), rest_rule['categories'][0]
seed = [dict(id=1, character='player', name=fatigue_name, category=fatigue_category, severity=severity,
             description='长途活动后感到疲劳，需要休息。', effects='持续活动较为吃力。', created_at='测试初始状态', updated_at='测试初始状态'),
        dict(id=2, character='player', name='左臂擦伤', category='伤势', severity='轻微',
             description='手臂有一处尚未愈合的擦伤。', effects='碰触时疼痛。', created_at='测试初始状态', updated_at='测试初始状态')]
(stage / 'location-status-seed.txt').write_text(json.dumps(seed, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
(stage / 'location-status-pattern.txt').write_text(r'"statuses":\[[^\]]*\]', encoding='utf-8')
(stage / 'location-next-status-pattern.txt').write_text(r'"next_status_id":\d+', encoding='utf-8')

def patch(path, old, new):
    text = path.read_text(encoding='utf-8-sig')
    assert old in text, path
    path.write_text(text.replace(old, new, 1), encoding='utf-8-sig')

patch(stage / 'ERB/SYSTEM_TITLE.ERB', '@SYSTEM_TITLE\n', '@SYSTEM_TITLE\nCALL LOCATION_ACTIONS_TEST\nQUIT\n')
patch(folder / '流程.ERB', '$SCENARIO_TEST_RAIN_ROOM_INPUT\nESCAPE 0\nINPUT',
      '$SCENARIO_TEST_RAIN_ROOM_INPUT\nESCAPE 0\nCALL LOCATION_CAPTURE_MENU\nRETURN')
patch(folder / '流程.ERB', '$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nINPUT',
      '$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nRESULT = 0')
# Keep exercising the compatibility 104/105 bridge in this isolated fixture.
# Production action 4 now opens the native map, covered by test_route_movement_ui.py.
common = folder / '常用行动.ERB'
text = common.read_text(encoding='utf-8-sig')
start, end = text.index('@RAIN_ROOM_MOVE_PAGE\n'), text.index('@RAIN_ROOM_ACTION_ALLOWED')
text = text[:start] + '''@RAIN_ROOM_MOVE_PAGE
CLEARLINE LINECOUNT
RESULTS:0 '= SAVESTR:61
AICONNECTION 104
PRINTFORML %RESULTS:0%
CALL LOCATION_MOVE_INPUT
LOCAL:0 = RESULT
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= STRFORM("{LOCAL:0}")
RESULTS:2 '= STRFORM("{DAY}")
RESULTS:3 '= STRFORM("{TIME}")
AICONNECTION 105
IF RESULT < 0
    THROW legacy movement failed
ENDIF
SAVESTR:61 '= RESULTS:0
LOCALS:0 '= RESULTS:1
LOCAL:1 = TOINT(RESULTS:2)
LOCALS:1 '= RESULTS:3
FLAG:34 = 4
FLAG:35 = LOCAL:1
SAVESTR:20 '= "WAITING"
SAVESTR:21 '= "兼容移动测试"
SAVESTR:22 '= STRFORM("第{DAY}天")
SAVESTR:19 '= LOCALS:0
SAVESTR:62 '= LOCALS:1
SAVESTR:66 '= "[]"
CALL RAIN_ROOM_APPEND_LOCAL_MEMORY
CALL SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE
RETURN

''' + text[end:]
common.write_text(text, encoding='utf-8-sig')
patch(folder / '行动输入.ERB', '\nINPUTS\n', '\nCALL LOCATION_COMPOSE_INPUT\n')

(stage / 'ERB/LOCATION_ACTIONS_TEST.ERB').write_text('''@LOCATION_ACTIONS_TEST
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL INIT_DISPLAY_SETTINGS
CALL RAIN_ROOM_ENSURE_FOOD
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= "5"
RESULTS:2 '= STRFORM("{DAY}")
RESULTS:3 '= STRFORM("{TIME}")
AICONNECTION 105
IF RESULT >= 0 || DAY != 1 || TIME != 0
    THROW non-adjacent movement was accepted
ENDIF
CALL SCENARIO_TEST_RAIN_ROOM_START
GLOBAL:875 = 2
CALL RAIN_ROOM_MOVE_PAGE
IF DAY != 1 || TIME != 1 || FLAG:20 != 10
    THROW first move did not use one tick
ENDIF
SAVETEXT SAVESTR:61, "location-first-move.txt", 0, 1
GLOBAL:875 = 4
CALL RAIN_ROOM_MOVE_PAGE
CALL SCENARIO_TEST_RAIN_ROOM_START
CALL RAIN_ROOM_LOCAL_ACTION
CALL LOCATION_SEED_STATUS
CALL RAIN_ROOM_SHORT_REST
SAVETEXT SAVESTR:61, "location-forest-rest.txt", 0, 1
SAVETEXT SAVESTR:62, "location-forest-rest-changes.txt", 0, 1
GLOBAL:875 = 2
CALL RAIN_ROOM_MOVE_PAGE
GLOBAL:875 = 5
CALL RAIN_ROOM_MOVE_PAGE
IF DAY != 1 || TIME != 5 || FLAG:20 != 10
    THROW travel or short rest changed daily food unexpectedly
ENDIF
SAVETEXT SAVESTR:61, "location-market.txt", 0, 1
CALL SCENARIO_TEST_RAIN_ROOM_START
CALL RAIN_ROOM_LOCAL_ACTION
GLOBAL:875 = 2
CALL RAIN_ROOM_MOVE_PAGE
GLOBAL:875 = 1
CALL RAIN_ROOM_MOVE_PAGE
IF DAY != 1 || TIME != 7
    THROW return route used the wrong movement cost
ENDIF
CALL LOCATION_SEED_STATUS
CALL RAIN_ROOM_SHORT_REST
IF DAY != 2 || TIME != 0 || FLAG:20 != 8
    THROW midnight short rest did not charge exactly two food
ENDIF
SAVETEXT SAVESTR:61, "location-cabin-rest.txt", 0, 1
SAVETEXT SAVESTR:62, "location-cabin-rest-changes.txt", 0, 1
CALL LOCATION_SEED_STATUS
CALL RAIN_ROOM_SLEEP
IF DAY != 3 || TIME != 0 || FLAG:20 != 6
    THROW sleep did not charge exactly two food
ENDIF
SAVETEXT SAVESTR:61, "location-cabin-sleep.txt", 0, 1
SAVETEXT SAVESTR:62, "location-cabin-sleep-changes.txt", 0, 1
SAVETEXT "PASS", "location-actions-pass.txt", 0, 1
QUIT

@LOCATION_SEED_STATUS
SAVESTR:61 '= REPLACE(SAVESTR:61, LOADTEXT("location-status-pattern.txt", 0, 1), UNICODE(34) + "statuses" + UNICODE(34) + ":" + LOADTEXT("location-status-seed.txt", 0, 1))
SAVESTR:61 '= REPLACE(SAVESTR:61, LOADTEXT("location-next-status-pattern.txt", 0, 1), UNICODE(34) + "next_status_id" + UNICODE(34) + ":3")
RETURN

@LOCATION_CAPTURE_MENU
GLOBAL:873 += 1
LOCALS:0 '= ""
FOR LOCAL:0, 0, LINECOUNT
    LOCALS:0 '= LOCALS:0 + GETDISPLAYLINE(LOCAL:0) + "||"
NEXT
SAVETEXT LOCALS:0, STRFORM("location-menu-{GLOBAL:873}.txt"), 0, 1
RETURN

@LOCATION_MOVE_INPUT
GLOBAL:876 += 1
LOCALS:0 '= ""
FOR LOCAL:0, 0, LINECOUNT
    LOCALS:0 '= LOCALS:0 + GETDISPLAYLINE(LOCAL:0) + "||"
NEXT
SAVETEXT LOCALS:0, STRFORM("location-move-menu-{GLOBAL:876}.txt"), 0, 1
RETURN GLOBAL:875

@LOCATION_COMPOSE_INPUT
GLOBAL:874 += 1
LOCALS:0 '= ""
FOR LOCAL:0, 0, LINECOUNT
    LOCALS:0 '= LOCALS:0 + GETDISPLAYLINE(LOCAL:0) + "||"
NEXT
SAVETEXT LOCALS:0, STRFORM("location-compose-{GLOBAL:874}.txt"), 0, 1
RESULTS:0 '= "0"
RETURN
''', encoding='utf-8-sig')

process = subprocess.Popen([str(stage / 'EraAI.exe')])
try:
    marker = stage / 'location-actions-pass.txt'
    deadline = time.monotonic() + 35
    while process.poll() is None and not marker.exists() and time.monotonic() < deadline:
        time.sleep(.1)
    if not marker.exists():
        for log in stage.glob('*.log'):
            print(log.read_text(encoding='utf-8-sig', errors='replace')[-6000:])
    assert marker.exists(), 'Location action engine test did not complete'
    menus = [(stage / f'location-menu-{i}.txt').read_text(encoding='utf-8-sig') for i in range(1, 4)]
    for menu in menus:
        compact = menu.replace(' ', '').replace('\u3000', '')
        for label in ['[1]自由输入', '[2]入眠', '[3]休息', '[4]移动']:
            # Disabled actions retain their numeric label as plain, noninteractive text.
            assert label in compact, (label, menu)
        assert '[12]查看场景' in compact and '[10]查看人物状态' in compact
    assert '[5] 狩猎' not in menus[0] and '[5] 交易' not in menus[0]
    assert re.search(r'\[5\]\s*狩猎', menus[1]) and not re.search(r'\[5\]\s*交易', menus[1])
    assert re.search(r'\[5\]\s*交易', menus[2]) and not re.search(r'\[5\]\s*狩猎', menus[2])
    for menu in menus[1:]:
        assert re.search(r'移动.*?[-─━—=]{4,}.*?\[5\]', menu), 'Missing divider before location action'
    first_move_menu = (stage / 'location-move-menu-1.txt').read_text(encoding='utf-8-sig')
    assert '林间小路' in first_move_menu and '屋外不远的溪边' in first_move_menu and '狩猎林地' not in first_move_menu
    assert '狩猎' in (stage / 'location-compose-1.txt').read_text(encoding='utf-8-sig')
    trade = (stage / 'location-compose-2.txt').read_text(encoding='utf-8-sig')
    assert '交易' in trade and '询问这里的货物与价格' in trade and '询问收购价格' in trade
    for file, location in [('location-first-move.txt', '林间小路'), ('location-market.txt', '村落集市')]:
        state = json.loads((stage / file).read_text(encoding='utf-8-sig'))
        assert state['scene']['location'] == location and state['character_locations']['player'] == location
        assert state['character_locations']['ella'] == '森林边的小屋' and 'ella' not in state['scene']['present_characters']
    forest = json.loads((stage / 'location-forest-rest.txt').read_text(encoding='utf-8-sig'))
    cabin = json.loads((stage / 'location-cabin-rest.txt').read_text(encoding='utf-8-sig'))
    slept = json.loads((stage / 'location-cabin-sleep.txt').read_text(encoding='utf-8-sig'))
    def remaining(state):
        return next((entry for entry in state['statuses'] if entry['id'] == 1 and entry['character'] == 'player' and entry['category'] == '疲劳'), None)
    def check_recovery(state, levels):
        left = severity_level - levels
        actual = remaining(state)
        assert actual is None if left <= 0 else actual and actual['severity'] == rest_rule['severity_labels'][left - 1]
        assert actual is None or actual['name'] == '疲劳', 'Old fatigue names were not normalized'
        assert next(entry for entry in state['statuses'] if entry['name'] == '左臂擦伤') == seed[1], 'Rest healed an unrelated wound'
    forest_levels = rest_rule['levels'] + rest_rule.get('location_bonus', {}).get('4', 0)
    cabin_levels = rest_rule['levels'] + rest_rule['location_bonus']['1']
    assert cabin_levels > forest_levels, 'Cabin lacks a mechanical rest bonus'
    check_recovery(forest, forest_levels)
    check_recovery(cabin, cabin_levels)
    check_recovery(slept, sleep_rule['levels'] + sleep_rule['location_bonus']['1'])
    for file in ['location-forest-rest-changes.txt', 'location-cabin-rest-changes.txt', 'location-cabin-sleep-changes.txt']:
        assert (stage / file).read_text(encoding='utf-8-sig').strip(), 'Recovery changes were lost before the result page'
    assert not (stage / '数据/账户/api-usage.json').exists(), 'Local movement/rest or cancelled trade requested an API'
    print('PASS actual location menus, exclusive hunt/trade, adjacent player-only movement, cabin recovery and daily food; no API.')
finally:
    if process.poll() is None:
        process.terminate()
    process.wait(timeout=10)
