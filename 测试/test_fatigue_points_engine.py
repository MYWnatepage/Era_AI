"""Actual ERB preview/fallback/adoption/replay/rest bridges; no HTTP or paid API."""
import sys
sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
from test_support import TEST_ROOT, GAME_DIR
import json
import re
import shutil
import subprocess
import time

stage = TEST_ROOT / 'fatigue-points-engine'
shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns(
    '配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config', '.era-ai-session.lock'))
(stage / '配置').mkdir()
(stage / '配置/music-settings.json').write_text(json.dumps(dict(Enabled=False, Volume=50)), encoding='utf-8')
folder = stage / 'ERB/script/艾拉的奇幻冒险'
definition = folder / '剧本.json'
scenario = json.loads(definition.read_text(encoding='utf-8-sig'))
scenario['initial_scene'] = dict(location='狩猎林地', present_characters=['player'], summary='你在林中停下脚步，准备追踪猎物。')
scenario['fatigue_rules']['enabled'] = True
scenario['fatigue_rules']['initial_points'] = 50
scenario['fatigue_rules']['hunt_cost'] = 25
scenario['fatigue_rules']['rest_recovery'] = 15
scenario['fatigue_rules']['dice_penalties'] = [0, 2, 4, 6]
definition.write_text(json.dumps(scenario, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
title = stage / 'ERB/SYSTEM_TITLE.ERB'
source = title.read_text(encoding='utf-8-sig')
assert '@SYSTEM_TITLE\n' in source
title.write_text(source.replace('@SYSTEM_TITLE\n', '@SYSTEM_TITLE\nCALL FATIGUE_POINTS_ENGINE_TEST\nQUIT\n', 1), encoding='utf-8-sig')

(stage / 'ERB/FATIGUE_POINTS_ENGINE_TEST.ERB').write_text('''@FATIGUE_POINTS_ENGINE_TEST
#DIMS FATIGUE_ORIGINAL
#DIMS FATIGUE_CANDIDATE
#DIMS FATIGUE_ADOPTED
#DIMS FATIGUE_PREVIEW
#DIM FATIGUE_GAIN
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL INIT_DISPLAY_SETTINGS
CALL RAIN_ROOM_ENSURE_FOOD
FATIGUE_ORIGINAL '= SAVESTR:61
SAVETEXT FATIGUE_ORIGINAL, "fatigue-original.txt", 0, 1
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= "17"
RESULTS:2 '= "hunt"
AICONNECTION 40
IF RESULT < 0
    THROW fatigue dice preview failed
ENDIF
FATIGUE_PREVIEW '= RESULTS:0
FATIGUE_GAIN = TOINT(RESULTS:1)
SAVETEXT FATIGUE_PREVIEW, "fatigue-preview.txt", 0, 1
SAVETEXT STRFORM("{FATIGUE_GAIN}"), "fatigue-gain.txt", 0, 1
IF SAVESTR:61 != FATIGUE_ORIGINAL
    THROW preview changed live fatigue or inventory
ENDIF
; Prepare the API action, but never call AISTART or issue a network request.
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= "17"
RESULTS:2 '= "hunt"
AICONNECTION 41
IF RESULT < 0 || SAVESTR:61 != FATIGUE_ORIGINAL
    THROW preparing an action changed the current state
ENDIF
; Exercise the production local fallback with the exact same original roll.
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= "17"
RESULTS:2 '= "hunt"
AICONNECTION 42
IF RESULT < 0
    THROW fatigue fallback failed
ENDIF
FATIGUE_CANDIDATE '= RESULTS:0
SAVETEXT FATIGUE_CANDIDATE, "fatigue-candidate.txt", 0, 1
SAVETEXT RESULTS:2, "fatigue-changes.txt", 0, 1
IF SAVESTR:61 != FATIGUE_ORIGINAL
    THROW fallback committed before adoption
ENDIF
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= FATIGUE_CANDIDATE
AICONNECTION 35
IF RESULT < 0
    THROW fatigue candidate adoption failed
ENDIF
SAVESTR:61 '= RESULTS:0
FATIGUE_ADOPTED '= SAVESTR:61
SAVETEXT FATIGUE_ADOPTED, "fatigue-adopted.txt", 0, 1
; The same candidate is stale once adopted. It must not debit or credit twice.
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= FATIGUE_CANDIDATE
AICONNECTION 35
IF RESULT >= 0 || SAVESTR:61 != FATIGUE_ADOPTED
    THROW duplicate adoption was not rejected atomically
ENDIF
SAVETEXT SAVESTR:61, "fatigue-after-replay.txt", 0, 1
RESULTS:0 '= SAVESTR:61
AICONNECTION 87
IF RESULT < 0
    THROW forest rest failed
ENDIF
IF SAVESTR:61 != FATIGUE_ADOPTED
    THROW rest bridge changed source before accepting its result
ENDIF
SAVESTR:61 '= RESULTS:0
SAVETEXT SAVESTR:61, "fatigue-rested.txt", 0, 1
SAVETEXT STRFORM("{DAY},{TIME}"), "fatigue-clock.txt", 0, 1
SAVETEXT "PASS", "fatigue-points-pass.txt", 0, 1
QUIT
''', encoding='utf-8-sig')

process = subprocess.Popen([str(stage / 'EraAI.exe')])
try:
    marker = stage / 'fatigue-points-pass.txt'
    deadline = time.monotonic() + 35
    while process.poll() is None and not marker.exists() and time.monotonic() < deadline:
        time.sleep(.1)
    if not marker.exists():
        for log in stage.glob('*.log'):
            print(log.read_text(encoding='utf-8-sig', errors='replace')[-6000:])
    assert marker.exists(), 'Actual fatigue bridge test did not complete'

    def load(name):
        return json.loads((stage / name).read_text(encoding='utf-8-sig'))

    original = load('fatigue-original.txt')
    candidate = load('fatigue-candidate.txt')
    adopted = load('fatigue-adopted.txt')
    replayed = load('fatigue-after-replay.txt')
    rested = load('fatigue-rested.txt')
    preview = (stage / 'fatigue-preview.txt').read_text(encoding='utf-8-sig')
    gain = int((stage / 'fatigue-gain.txt').read_text(encoding='utf-8-sig'))
    expected = next(band['quantity'] for band in scenario['actions']['hunt']['outcomes'] if band['min'] <= 13 <= band['max'])
    assert re.search(r'原始\s*17.*疲劳\s*4.*有效\s*13', preview), preview
    assert gain == expected, 'Preview used original instead of effective die'
    assert original['fatigue_points'] == 50 and original['revision'] + 1 == candidate['revision']
    assert candidate['fatigue_points'] == 75 and candidate == adopted == replayed
    food = lambda state: next(item for item in state['items'] if item['id'] == 2)['quantities']['player']
    assert food(candidate) == food(original) + expected, 'Fallback did not use effective-roll reward'
    assert rested['fatigue_points'] == 60 and food(rested) == food(adopted)
    assert rested['revision'] == adopted['revision'] + 1
    assert rested['scene']['location'] == adopted['scene']['location'] == '狩猎林地'
    assert (stage / 'fatigue-clock.txt').read_text(encoding='utf-8-sig').strip() == '1,0'
    assert not (stage / '数据/账户/api-usage.json').exists(), 'Fallback bridge must not call an API'
    print('PASS actual fatigue preview 17 - 4 = 13, fallback/adoption once, stale replay rejected and forest rest 75 -> 60; no API.', flush=True)
finally:
    if process.poll() is None:
        process.terminate()
    process.wait(timeout=10)
