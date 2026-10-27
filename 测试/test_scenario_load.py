from test_support import TEST_ROOT, GAME_DIR, GAME_EXE
from pathlib import Path
import shutil
import subprocess
import time

root = TEST_ROOT
for case, selection, legacy in [('manual', 1, False), ('legacy_auto', 100, True), ('empty_return', 0, False), ('wrong_scenario', 1, False)]:
    stage = root / ('scenario_load_test_' + case)
    shutil.copytree(GAME_DIR, stage, dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns('配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config', 'ai-settings.json'))
    shutil.copy2(GAME_EXE, stage / 'EraAI.exe')
    for old in stage.glob('*.sav'):
        old.unlink()
    title = stage / 'ERB/SYSTEM_TITLE.ERB'
    text = title.read_text(encoding='utf-8-sig').replace('@SYSTEM_TITLE\n', '@SYSTEM_TITLE\nCALL LOAD_FLOW_TEST\nQUIT\n', 1)
    title.write_text(text, encoding='utf-8-sig')
    flow = stage / 'ERB/SYSTEM_SCENARIO_LOAD.ERB'
    text = flow.read_text(encoding='utf-8-sig')
    # Only the isolated test substitutes menu choices; production uses normal INPUT.
    main = stage / 'ERB/SYSTEM_MAIN.ERB'
    main.write_text(main.read_text(encoding='utf-8-sig').replace('ESCAPE 0\nINPUT', 'ESCAPE 0\nRESULT = 0', 1), encoding='utf-8-sig')
    text = text.replace('ESCAPE 0\nINPUT', f'ESCAPE 0\nRESULT = {selection}', 1)
    if case == 'wrong_scenario':
        text = text.replace('    PRINTL 无法识别存档所属剧本，请检查该剧本是否已安装且登记有效。\n    WAIT\n    BEGIN TITLE', '    SAVETEXT "PASS scenario load", "load_pass.txt", 0, 1\n    QUIT')
    text = text.replace('CALL SCENARIO_CALL_HOOK, 3\nBEGIN TITLE', '''IF DAY != 3 || FLAG:0 != 17 || SAVESTR:25 != "saved history"
    THROW 读档没有恢复剧情数据
ENDIF
IF GLOBAL:500 != 1 || GLOBAL:510 != 1 || SAVESTR:0 != "rain_room"
    THROW 读档没有恢复剧本和历史计数
ENDIF
SAVETEXT "PASS scenario load", "load_pass.txt", 0, 1
QUIT''')
    flow.write_text(text, encoding='utf-8-sig')
    scenario_label = '雨夜房间' if legacy else '艾拉的奇幻冒险'
    test = '''@LOAD_FLOW_TEST
CALL INIT_DISPLAY_SETTINGS
CALL APPLY_DISPLAY_SETTINGS
CALL LOAD_SCENARIO_SELECT
IF CHKDATA(0) != 1
    THROW 隔离测试存档不为空
ENDIF
'''
    if case != 'empty_return':
        test += f'''DAY = 3
FLAG:0 = 17
FLAG:2 = 1
GLOBAL:500 = 1
SAVESTR:0 '= "{'other_scenario' if case == 'wrong_scenario' else '' if legacy else 'rain_room'}"
SAVESTR:25 '= "saved history"
SAVEDATA 0, "2026/10/22 12:34:56 {scenario_label} 第3天 好感度17"
SAVEDATA 1, "其他剧本 第3天"
SAVEDATA 99, "自动存档：{scenario_label}"
CALL CHECK_RAIN_ROOM_SAVE(0)
IF RESULT != 1
    THROW 未识别手动存档
ENDIF
CALL CHECK_RAIN_ROOM_SAVE(1)
IF RESULT != 0
    THROW 其他剧本未被过滤
ENDIF
CALL CHECK_RAIN_ROOM_SAVE(99)
IF RESULT != 1
    THROW 未识别自动存档
ENDIF
CALL CHECK_RAIN_ROOM_SAVE(2)
IF RESULT != 0
    THROW 空存档未被过滤
ENDIF
DAY = 99
FLAG:0 = 99
SAVESTR:25 '= "unsaved history"
GLOBAL:500 = 0
GLOBAL:510 = 0
CALL LOAD_RAIN_ROOM_SAVES
THROW 未进入读档完成事件
'''
    else:
        test += '''CALL LOAD_RAIN_ROOM_SAVES
SAVETEXT "PASS scenario load", "load_pass.txt", 0, 1
QUIT
'''
    (stage / 'ERB/LOAD_FLOW_TEST.ERB').write_text(test, encoding='utf-8-sig')
    marker = stage / 'load_pass.txt'
    if marker.exists():
        marker.unlink()
    proc = subprocess.Popen([str((stage / 'EraAI.exe').resolve())])
    try:
        deadline = time.monotonic() + 20
        while proc.poll() is None and not marker.exists() and time.monotonic() < deadline:
            time.sleep(.2)
        assert marker.exists(), f'{case}: game engine did not finish test'
        assert marker.read_text(encoding='utf-8-sig').strip() == 'PASS scenario load'
    finally:
        if proc.poll() is None:
            proc.terminate()
    print('PASS: ' + case, flush=True)
