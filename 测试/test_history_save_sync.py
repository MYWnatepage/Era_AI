from test_support import TEST_ROOT, GAME_DIR, GAME_EXE
from pathlib import Path
import json
import shutil
import subprocess
import time
import uuid

root=TEST_ROOT
for case in ['snapshot','auto','legacy','newgame']:
    stage=root/('history_sync_'+case+'_'+uuid.uuid4().hex[:8])
    shutil.copytree(GAME_DIR,stage,ignore=shutil.ignore_patterns('配置','数据','*.sav','*.log','profile','setting.json','emuera.config','ai-settings.json'))
    shutil.copy2(GAME_EXE,stage/'EraAI.exe')
    memory=stage/'数据/记忆';memory.mkdir(parents=True)
    (memory/'rain_room.json').write_text(json.dumps([{'Time':'old','Player':'OLD_INPUT','Reply':'OLD_REPLY'}]),encoding='utf-8')
    title=stage/'ERB/SYSTEM_TITLE.ERB'
    title.write_text(title.read_text(encoding='utf-8-sig').replace('@SYSTEM_TITLE\n','@SYSTEM_TITLE\nCALL HISTORY_SYNC_ENGINE_TEST\nQUIT\n',1),encoding='utf-8-sig')
    flow=stage/'ERB/SYSTEM_SCENARIO_LOAD.ERB'
    expected='LEGACY_REPLY' if case=='legacy' else 'OLD_REPLY'
    flow.write_text(flow.read_text(encoding='utf-8-sig').replace('CALL SCENARIO_CALL_HOOK, 3\nBEGIN TITLE',f'''AIMEMORY "rain_room"
IF STRFIND(RESULTS:0, "{expected}") < 0 || STRFIND(RESULTS:0, "FUTURE_REPLY") >= 0
    THROW AI历史没有随存档恢复
ENDIF
SAVETEXT "PASS history sync", "history_sync_pass.txt", 0, 1
QUIT'''),encoding='utf-8-sig')
    script='''@HISTORY_SYNC_ENGINE_TEST
CALL INIT_DISPLAY_SETTINGS
CALL APPLY_DISPLAY_SETTINGS
SAVESTR:0 '= "rain_room"
FLAG:2 = 1
GLOBAL:500 = 1
'''
    if case=='newgame':
        script+='''CALL NEW_RAIN_ROOM_AI_HISTORY
AIMEMORY "rain_room"
IF STRFIND(RESULTS:0, "OLD_REPLY") >= 0
    THROW 新游戏串入旧历史
ENDIF
SAVETEXT "PASS history sync", "history_sync_pass.txt", 0, 1
QUIT
'''
    else:
        if case=='legacy':
            script+='''SAVESTR:60 '= ""
SAVESTR:23 '= "day1"
SAVESTR:24 '= "LEGACY_INPUT"
SAVESTR:25 '= "LEGACY_REPLY"
'''
        else:script+='CALL CAPTURE_RAIN_ROOM_AI_HISTORY\n'
        if case=='auto':script+='GLOBAL:300 = 1\nCALL GAME_AUTOSAVE\n'
        else:script+='SAVEDATA 0, "history sync test"\n'
        script+='''RESULTS:1 '= "rain_room"
RESULTS:2 '= "day9"
RESULTS:3 '= "FUTURE_INPUT"
RESULTS:4 '= "FUTURE_REPLY"
AICONNECTION 22
'''
        script+=f'LOADDATA {99 if case=="auto" else 0}\nTHROW 未进入加载事件\n'
    (stage/'ERB/HISTORY_SYNC_TEST.ERB').write_text(script,encoding='utf-8-sig')
    marker=stage/'history_sync_pass.txt'
    proc=subprocess.Popen([str((stage/'EraAI.exe').resolve())])
    try:
        deadline=time.monotonic()+25
        while proc.poll() is None and not marker.exists() and time.monotonic()<deadline:time.sleep(.2)
        assert marker.exists(),case+': history sync test failed'
        data=json.loads((memory/'rain_room.json').read_text(encoding='utf-8-sig'))
        assert len(data)==(0 if case=='newgame' else 1)
        assert (memory/'rain_room.json.before-save-sync').exists()
    finally:
        if proc.poll() is None:proc.terminate()
    print('PASS history save sync: '+case,flush=True)
