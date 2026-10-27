from test_support import TEST_ROOT, GAME_DIR, GAME_EXE
from pathlib import Path
import json, shutil, subprocess, time

stage=TEST_ROOT/'window-size'
shutil.copytree(GAME_DIR,stage,ignore=shutil.ignore_patterns('配置','数据','*.sav','*.log','profile','setting.json','emuera.config'))
title=stage/'ERB/SYSTEM_TITLE.ERB'
text=title.read_text(encoding='utf-8-sig').replace('@SYSTEM_TITLE\n','@SYSTEM_TITLE\nCALL WINDOW_TEST\nQUIT\n',1)
title.write_text(text, encoding='utf-8-sig')
title = stage / 'ERB/SYSTEM_DISPLAY.ERB'
text = title.read_text(encoding='utf-8-sig')

a=text.index('@TITLE_SETTING_WINDOW_SIZE\n');b=text.index('\n@',a+1)
page=text[a:b].replace('\nINPUT\n','''
GLOBAL:970 += 1
SELECTCASE GLOBAL:970
    CASE 1
        RESULT = 1
    CASE 2, 3
        RESULT = 7
    CASE 4
        RESULT = 6
    CASE 5
        RESULT = 3
    CASEELSE
        RESULT = 0
ENDSELECT
''',1)
page=page.replace('        INPUTS','''        IF GLOBAL:970 == 2
            RESULTS:0 '= ""
        ELSE
            RESULTS:0 '= "invalid size"
        ENDIF''').replace('WAIT\n','; test continues\n')
text=text[:a]+page+text[b:]
title.write_text(text,encoding='utf-8-sig')
test=stage/'ERB/WINDOW_TEST.ERB'
test.write_text('''@WINDOW_TEST
CALL INIT_DISPLAY_SETTINGS
CALL APPLY_DISPLAY_SETTINGS
IF GAMEBASE_TITLE != "Era_AI"
    THROW 游戏名称未修改
ENDIF
CALL TITLE_SETTING_WINDOW_SIZE
DISPLAYINFO
IF RESULTS:8 != "普通窗口" || TOINT(RESULTS:6) > 1280 || TOINT(RESULTS:7) > 800
    THROW 预设、取消或返回未正确生效
ENDIF
LOCALS:0 '= RESULTS:6
LOCALS:1 '= RESULTS:7
RESULTS:0 '= ""
DISPLAYTYPE 6
DISPLAYINFO
IF RESULTS:6 != LOCALS:0 || RESULTS:7 != LOCALS:1
    THROW 取消输入改变了尺寸
ENDIF
RESULTS:0 '= "7680x4320"
DISPLAYTYPE 6
DISPLAYINFO
IF TOINT(RESULTS:6) > TOINT(RESULTS:9) || TOINT(RESULTS:7) > TOINT(RESULTS:10)
    THROW 超出屏幕的尺寸未限制
ENDIF
DISPLAYTYPE 7
DISPLAYINFO
IF RESULTS:8 != "最大化"
    THROW 最大化未生效
ENDIF
SAVETEXT "PASS window", "window-pass.txt", 0, 1
QUIT
''',encoding='utf-8-sig')
def run(marker):
    p=subprocess.Popen([str((stage/'EraAI.exe').resolve())])
    try:
        deadline=time.monotonic()+30
        while p.poll() is None and not marker.exists() and time.monotonic()<deadline:time.sleep(.15)
        assert marker.exists(),'Window engine test failed'
    finally:
        if p.poll() is None:p.terminate();p.wait(timeout=5)
run(stage/'window-pass.txt')
settings=json.loads((stage/'配置/window-settings.json').read_text())
assert settings['Maximized'] is True
test.write_text('''@WINDOW_TEST
CALL INIT_DISPLAY_SETTINGS
CALL APPLY_DISPLAY_SETTINGS
DISPLAYINFO
IF RESULTS:8 != "最大化"
    THROW 重启后没有保留最大化
ENDIF
RESULTS:0 '= "1024 640"
DISPLAYTYPE 6
DISPLAYINFO
IF RESULTS:8 != "普通窗口" || RESULTS:6 != "1024" || RESULTS:7 != "640"
    THROW 最大化后无法恢复普通尺寸
ENDIF
SAVETEXT "PASS restart", "restart-pass.txt", 0, 1
QUIT
''',encoding='utf-8-sig')
run(stage/'restart-pass.txt')
print('PASS: window menu, presets, custom validation/cancel, screen fitting, maximize persistence and restore.')
