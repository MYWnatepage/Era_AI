"""Exercise real ERB menu branches with deterministic local input and no API service."""
import sys
sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
from test_support import TEST_ROOT, GAME_DIR
from pathlib import Path
import json, shutil, subprocess, time

stage = TEST_ROOT / 'story-menu-input'
shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns('配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
(stage/'配置').mkdir()
(stage/'配置/music-settings.json').write_text(json.dumps(dict(Enabled=False, Volume=50)), encoding='utf-8')

def patch(path, old, new):
    text = path.read_text(encoding='utf-8-sig')
    assert old in text, path
    path.write_text(text.replace(old,new,1), encoding='utf-8-sig')

patch(stage/'ERB/SYSTEM_TITLE.ERB', '@SYSTEM_TITLE\n', '@SYSTEM_TITLE\nCALL STORY_MENUS_TEST\nQUIT\n')
memory = stage/'ERB/历史记忆.ERB'
text = memory.read_text(encoding='utf-8-sig')
text = '\n'.join('CALL STORY_MENU_STRING' if line.strip() == 'INPUTS' else
                 'CALL STORY_MENU_NUMBER' if line.strip() == 'INPUT' else
                 '; no pause in deterministic test' if line.strip() == 'WAIT' else line for line in text.splitlines())
memory.write_text(text+'\n', encoding='utf-8-sig')
folder = stage/'ERB/script/艾拉的奇幻冒险'
assert '选中此物品' not in (folder/'当前物品.ERB').read_text(encoding='utf-8-sig')
assert 'RAIN_ROOM_ITEM_ACTION' not in (folder/'当前物品.ERB').read_text(encoding='utf-8-sig')
for name in ['当前物品.ERB', '物品行动.ERB']:
    file = folder/name
    text = file.read_text(encoding='utf-8-sig')
    text = '\n'.join('CALL STORY_MENU_STRING' if line.strip() == 'INPUTS' else
                     'CALL STORY_MENU_NUMBER' if line.strip() == 'INPUT' else line for line in text.splitlines())
    file.write_text(text+'\n',encoding='utf-8-sig')

choices = [1004,1,1,3,4,0,1005,1,1,1,0,0, 10011,0,0,0]
strings = ['在溪边发现旧桥已塌，需要另找道路。','=','溪边,旧桥',
           '主角与艾拉约定先整理明日出行清单。','=','出行,清单']
number_cases = '\n'.join(f'    CASE {i+1}\n        RETURN {v}' for i,v in enumerate(choices))
string_cases = '\n'.join(f'    CASE {i+1}\n        RESULTS:0 \'= "{v}"' for i,v in enumerate(strings))
(stage/'ERB/STORY_MENUS_TEST.ERB').write_text(f'''@STORY_MENUS_TEST
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL INIT_DISPLAY_SETTINGS
CALL RAIN_ROOM_ENSURE_FOOD
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= "11"
AICONNECTION 60
IF RESULT < 0 || RESULTS:5 != "ella"
    THROW companion-held item did not retain its actual owner
ENDIF
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= "11"
RESULTS:2 '= "ella"
AICONNECTION 63
IF RESULT < 0
    THROW explicit companion item context was rejected
ENDIF
CALL NEW_RAIN_ROOM_AI_HISTORY
RESULTS:1 '= "rain_room"
RESULTS:2 '= "第1天 时间0/8"
RESULTS:3 '= "明天出门前先列清单。"
RESULTS:4 '= "艾拉点头答应，决定先整理需要携带的物品。"
AICONNECTION 22
CALL NATIVE_MEMORY_PAGE
SAVETEXT SAVESTR:61, "menu-memory.txt", 0, 1
CALL RAIN_ROOM_INVENTORY_PAGE, 1
CALL RAIN_ROOM_WORLD_PAGE
IF GLOBAL:821 != {len(choices)} || GLOBAL:822 != {len(strings)}
    THROW menu input sequence did not reach every expected branch
ENDIF
IF DAY != 1 || TIME != 0
    THROW browsing and editing memory advanced game time
ENDIF
SAVETEXT "PASS", "menu-pass.txt", 0, 1
QUIT

@STORY_MENU_NUMBER
GLOBAL:821 += 1
SELECTCASE GLOBAL:821
{number_cases}
    CASEELSE
        THROW unexpected extra numeric input
ENDSELECT

@STORY_MENU_STRING
GLOBAL:822 += 1
SELECTCASE GLOBAL:822
{string_cases}
    CASEELSE
        THROW unexpected extra text input
ENDSELECT
RETURN
''',encoding='utf-8-sig')
process = subprocess.Popen([str(stage/'EraAI.exe')])
try:
    marker = stage/'menu-pass.txt'
    deadline = time.monotonic()+25
    while process.poll() is None and not marker.exists() and time.monotonic()<deadline: time.sleep(.1)
    if not marker.exists():
        for log in stage.glob('*.log'): print(log.read_text(encoding='utf-8-sig',errors='replace')[-5000:])
    assert marker.exists(), 'real menu input flow did not complete'
    state=json.loads((stage/'menu-memory.txt').read_text(encoding='utf-8-sig'))
    assert len(state['memories'])==2
    assert state['memories'][0]['pinned'] and not state['memories'][0]['active']
    assert state['memories'][1]['summary']=='主角与艾拉约定先整理明日出行清单。'
    assert state['memories'][1]['time']=='第1天 时间0/8'
    print('PASS actual menus: manual memory, pin/inactive, history excerpt, readonly item details and world return; no API requests')
finally:
    if process.poll() is None: process.terminate()
    process.wait(timeout=10)
