"""Inspect bridge 85's real native button tree in a disposable player, with no API connection."""
from test_support import TEST_ROOT, PROJECT_ROOT, GAME_DIR
import json, shutil, subprocess

stage = TEST_ROOT / 'inventory-grid-render'
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
RESULTS:0 '= "rain_room"
AICONNECTION 47
AICONNECTION 30
SAVESTR:61 '= RESULTS:0
FOR LOCAL:0, 1, 26
    RESULTS:0 '= SAVESTR:61
    RESULTS:1 '= STRFORM("{LOCAL:0}")
    RESULTS:2 '= STRFORM("物品{LOCAL:0}")
    IF LOCAL:0 % 3 == 0
        RESULTS:2 '= STRFORM("高碳钢长矛{LOCAL:0}")
    ELSEIF LOCAL:0 % 3 == 1
        RESULTS:2 '= STRFORM("铜币{LOCAL:0}")
    ENDIF
    IF LOCAL:0 == 18
        RESULTS:2 '= "轻柔🌿🩹长物品名称🌿🩹组合字符用于检查宽度截断🌿🩹"
    ENDIF
    RESULTS:3 '= "用于本地渲染回归的资料。"
    RESULTS:4 '= "普通物品"
    RESULTS:5 '= ""
    RESULTS:6 '= "1"
    RESULTS:7 '= "player"
    RESULTS:8 '= ""
    AICONNECTION 31
    IF RESULT < 0
        THROW inventory grid fixture item creation failed
    ENDIF
    SAVESTR:61 '= RESULTS:0
NEXT
SAVETEXT "setup", "grid-stage.txt", 0, 1
INPUTS
CALL INVENTORY_GRID_RENDER_DRAW, 0, "narrow-first"
CALL INVENTORY_GRID_RENDER_DRAW, 3, "narrow-last"
CALL INVENTORY_GRID_RENDER_DRAW, 3, "wide-clamped"
SAVETEXT "PASS", "grid-pass.txt", 0, 1
QUIT

@INVENTORY_GRID_RENDER_DRAW, ARG:0, ARGS:0
CLEARLINE LINECOUNT
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= STRFORM("{ARG:0}")
RESULTS:2 '= "2,5,17,25"
RESULTS:3 '= "select"
AICONNECTION 85
IF RESULT < 0
    THROW inventory grid renderer rejected a valid fixture
ENDIF
LOCALS:0 '= STRFORM("%RESULTS:0%|%RESULTS:3%|%RESULTS:4%")
SAVETEXT LOCALS:0, "grid-data.txt", 0, 1
SAVETEXT ARGS:0, "grid-stage.txt", 0, 1
INPUTS
RETURN
''', encoding='utf-8-sig')
assembly = PROJECT_ROOT / '源码/Emuera/artifacts/bin/Emuera/release-naudio_win-x64/EraAI.dll'
assert assembly.exists(), 'Build the native player before the UI render test.'
subprocess.run(['dotnet', 'run', '--no-build', '--project', str(PROJECT_ROOT / '测试/NativeAI.Tests.csproj'),
                '-c', 'Release', '--', '--inventory-grid-ui', str(assembly), str(stage)], check=True, timeout=50)
assert (stage / 'grid-pass.txt').exists(), 'Native grid fixture did not finish.'
assert not (stage / '数据/账户/api-usage.json').exists(), 'Rendering must never issue an API request.'
print('PASS: actual inventory button tree, fixed pixel columns, persistent chosen color and resize page correction; no API.', flush=True)
