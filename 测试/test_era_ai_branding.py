from test_support import TEST_ROOT, GAME_DIR, GAME_EXE
from pathlib import Path
import shutil,subprocess,time,uuid
root=TEST_ROOT
stage=root/('branding_test_'+uuid.uuid4().hex[:8])
shutil.copytree(GAME_DIR,stage,ignore=shutil.ignore_patterns('配置','数据','*.sav','*.log','profile','setting.json','emuera.config','ai-settings.json'))
shutil.copy2(GAME_EXE,stage/'EraAI.exe')
p=stage/'ERB/SYSTEM_TITLE.ERB'
s=p.read_text(encoding='utf-8-sig').replace('$TITLE_INPUT\n','$TITLE_INPUT\nCALL CHECK_TITLE_BRANDING\nQUIT\n',1)
a=s.index('@TITLE_ABOUT\n');b=s.index('\n@',a+1)
s=s[:a]+s[a:b].replace('\nINPUT\n','\nRESULT = 0\n',1)+s[b:]
p.write_text(s,encoding='utf-8-sig')
(stage/'ERB/BRANDING_TEST.ERB').write_text('''@CHECK_TITLE_BRANDING
IF GAMEBASE_TITLE != "Era AI" || GAMEBASE_VERSIONNAME != "0.2.1 Alpha · 开发测试版"
    THROW 名称或版本元数据不正确
ENDIF
CALL CHECK_BRANDING_TEXT
CALL TITLE_ABOUT
CALL CHECK_BRANDING_TEXT
SAVETEXT "PASS branding", "branding_pass.txt", 0, 1
RETURN

@CHECK_BRANDING_TEXT
LOCAL:0 = 0
FOR LOCAL:1, 0, LINECOUNT
    LOCALS:0 '= GETDISPLAYLINE(LOCAL:1)
    IF STRFIND(LOCALS:0, "原生整合版") >= 0 || STRFIND(LOCALS:0, "版本 1.0") >= 0
        THROW 残留旧名称或版本
    ENDIF
    IF STRFIND(LOCALS:0, "版本：0.2.1 Alpha · 开发测试版") >= 0
        LOCAL:0 += 1
    ENDIF
NEXT
IF LOCAL:0 != 1
    THROW 页面未显示统一版本
ENDIF
RETURN
''',encoding='utf-8-sig')
marker=stage/'branding_pass.txt'
proc=subprocess.Popen([str((stage/'EraAI.exe').resolve())])
try:
    deadline=time.monotonic()+25
    while proc.poll() is None and not marker.exists() and time.monotonic()<deadline:time.sleep(.2)
    assert marker.exists(),'Branding test failed'
finally:
    if proc.poll() is None:proc.terminate()
print('PASS: main menu and about show Era AI with the same 0.2.1 Alpha version.')
