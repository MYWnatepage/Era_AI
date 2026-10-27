from test_support import TEST_ROOT, GAME_DIR
import shutil, subprocess, time
stage = TEST_ROOT / 'food_rule'
shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns('配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
title = stage / 'ERB/SYSTEM_TITLE.ERB'
title.write_text(title.read_text(encoding='utf-8-sig').replace('@SYSTEM_TITLE\n', '@SYSTEM_TITLE\nCALL FOOD_RULE_TEST\nQUIT\n', 1), encoding='utf-8-sig')
loader = stage / 'ERB/SYSTEM_SCENARIO_LOAD.ERB'
loader.write_text(loader.read_text(encoding='utf-8-sig').split('@EVENTLOAD')[0] + '@EVENTLOAD\nCALL FOOD_RULE_AFTER_LOAD\nQUIT\n', encoding='utf-8-sig')
state = stage / 'ERB/script/艾拉的奇幻冒险/状态管理.ERB'
state.write_text(state.read_text(encoding='utf-8-sig').replace('WAIT\nBEGIN TITLE', 'SAVETEXT "PASS food rule", "food_pass.txt", 0, 1\nQUIT'), encoding='utf-8-sig')
(stage / 'ERB/FOOD_RULE_TEST.ERB').write_text('''@FOOD_RULE_TEST
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL RAIN_ROOM_ENSURE_FOOD
IF FLAG:20 != 10 || FLAG:21 != 1 || FLAG:22 != 0
    THROW initial food
ENDIF
REPEAT 7
    CALL RAIN_ROOM_ADVANCE_TIME
REND
IF FLAG:20 != 10 || DAY != 1 || TIME != 7
    THROW premature deduction
ENDIF
CALL RAIN_ROOM_ADVANCE_TIME
IF FLAG:20 != 8 || DAY != 2 || TIME != 0 || FLAG:22 != 1
    THROW first daily deduction
ENDIF
; A repeated settlement in the same day is idempotent.
DAY = 1
CALL RAIN_ROOM_SETTLE_FOOD
IF FLAG:20 != 8
    THROW duplicate deduction
ENDIF
DAY = 2
SAVEDATA 0, "food roundtrip"
FLAG:20 = 99
FLAG:22 = 99
DAY = 99
LOADDATA 0
THROW load did not enter event

@FOOD_RULE_AFTER_LOAD
IF FLAG:20 != 8 || DAY != 2 || TIME != 0 || FLAG:22 != 1
    THROW food save restoration
ENDIF
CALL RAIN_ROOM_ENSURE_FOOD
IF FLAG:20 != 8
    THROW load reset food
ENDIF
REPEAT 32
    CALL RAIN_ROOM_ADVANCE_TIME
REND
IF FLAG:20 != 0 || DAY != 5 || TIME != 0
    THROW fifth settlement must exhaust food
ENDIF
CALL RAIN_ROOM_ADVANCE_TIME
IF RESULT != 0 || FLAG:20 != 0 || DAY != 5
    THROW depleted game must not advance
ENDIF
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL RAIN_ROOM_ENSURE_FOOD
IF FLAG:20 != 10 || DAY != 1
    THROW new game must reset food
ENDIF
; Legacy save initializes at its current day without retroactive consumption.
DAY = 9
SAVESTR:61 '= ""
FLAG:21 = 0
CALL RAIN_ROOM_ENSURE_FOOD
IF FLAG:20 != 10 || FLAG:22 != 8
    THROW legacy migration
ENDIF
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= "1"
RESULTS:2 '= "2"
RESULTS:3 '= "player"
AICONNECTION 33
SAVESTR:61 '= RESULTS:0
CALL RAIN_ROOM_SETTLE_FOOD
IF FLAG:20 != 0
    THROW partial supply must clamp to zero
ENDIF
; Starting/loading an exhausted run must take the actual ending path.
CALL SCENARIO_TEST_RAIN_ROOM_START
THROW exhausted scenario continued
''', encoding='utf-8-sig')
proc = subprocess.Popen([str(stage / 'EraAI.exe')])
try:
    marker = stage / 'food_pass.txt'
    deadline = time.monotonic() + 25
    while not marker.exists() and proc.poll() is None and time.monotonic() < deadline:
        time.sleep(.2)
    if not marker.exists():
        for log in stage.rglob('*.log'):
            print(log.read_text(encoding='utf-8-sig', errors='replace')[-6000:])
    assert marker.exists(), 'Engine food-rule test failed'
    print('PASS: daily consumption, exhaustion, idempotence, save/load, new game, legacy migration, ending')
finally:
    if proc.poll() is None:
        proc.terminate()
    proc.wait(timeout=10)
