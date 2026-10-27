"""Inspect bridge 85's real native button tree in a disposable player, with no API connection."""
from test_support import TEST_ROOT, PROJECT_ROOT, GAME_DIR
import json, shutil, subprocess, os

stage = TEST_ROOT / 'item-quantity-interaction'
os.environ['ERA_QUANTITY_PREVIEW_DIR'] = str(PROJECT_ROOT.parent / 'diagnostics')
flow = (GAME_DIR / 'ERB/script/艾拉的奇幻冒险/流程.ERB').read_text(encoding='utf-8-sig')
assert not any('狩猎' in line and line.strip().startswith(('PRINT', 'PRINTFORM')) for line in flow.splitlines()[:190]), 'Global menu must not expose hunt'
shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns('配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
(stage / '配置').mkdir()
(stage / '配置/music-settings.json').write_text(json.dumps(dict(Enabled=False, Volume=50)), encoding='utf-8')
(stage / '配置/typography-settings.json').write_text(json.dumps(dict(FontName='Microsoft YaHei UI', FontSize=24, LineHeight=32)), encoding='utf-8')
title = stage / 'ERB/SYSTEM_TITLE.ERB'
text = title.read_text(encoding='utf-8-sig')
assert '@SYSTEM_TITLE\n' in text
title.write_text(text.replace('@SYSTEM_TITLE\n', '@SYSTEM_TITLE\nCALL INVENTORY_GRID_RENDER_TEST\nQUIT\n', 1), encoding='utf-8-sig')
(stage / 'ERB/INVENTORY_GRID_RENDER_TEST.ERB').write_text('''@INVENTORY_GRID_RENDER_TEST
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL INIT_DISPLAY_SETTINGS
CALL APPLY_DISPLAY_SETTINGS
CALL RAIN_ROOM_ENSURE_FOOD
PRINT 当前状态：
RESULTS:0 '= "3"
RESULTS:1 '= "8"
AICONNECTION 95
PRINTL
INPUTS
SAVESTR:90 '= SAVESTR:61
CALL RAIN_ROOM_COMPOSE
IF SAVESTR:90 != SAVESTR:61 || TIME != 0
    THROW quantity gestures changed inventory or time
ENDIF
SAVETEXT "PASS", "quantity-pass.txt", 0, 1
QUIT
''', encoding='utf-8-sig')
assembly = PROJECT_ROOT / '源码/Emuera/artifacts/bin/Emuera/release-naudio_win-x64/EraAI.dll'
assert assembly.exists(), 'Build the native player before the UI render test.'
subprocess.run(['dotnet', 'run', '--no-build', '--project', str(PROJECT_ROOT / '测试/NativeAI.Tests.csproj'),
                '-c', 'Release', '--', '--item-quantity-ui', str(assembly), str(stage)], check=True, timeout=50)
assert (stage / 'quantity-pass.txt').exists(), 'Native grid fixture did not finish.'
assert not (stage / '数据/账户/api-usage.json').exists(), 'Rendering must never issue an API request.'
print('PASS: actual inventory button tree, fixed pixel columns, persistent chosen color and resize page correction; no API.', flush=True)
