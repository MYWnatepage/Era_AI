from test_support import TEST_ROOT, GAME_DIR, GAME_EXE
from pathlib import Path
import shutil,subprocess,time,uuid
root=TEST_ROOT
stage=root/('branding_test_'+uuid.uuid4().hex[:8])
shutil.copytree(GAME_DIR,stage,ignore=shutil.ignore_patterns('配置','数据','*.sav','*.log','profile','setting.json','emuera.config','ai-settings.json'))
shutil.copy2(GAME_EXE,stage/'EraAI.exe')
p=stage/'ERB/SYSTEM_TITLE.ERB'
s=p.read_text(encoding='utf-8-sig').replace('$TITLE_INPUT\n','$TITLE_INPUT\nCALL CHECK_TITLE_BRANDING\nQUIT\n',1)
p.write_text(s,encoding='utf-8-sig')
p=stage/'ERB/SYSTEM_ABOUT.ERB'
s=p.read_text(encoding='utf-8-sig')
a=s.index('@TITLE_ABOUT\n');b=len(s)
s=s[:a]+s[a:b].replace('\nINPUT\n','\nRESULT = 0\n',1)+s[b:]
p.write_text(s,encoding='utf-8-sig')
(stage/'ERB/BRANDING_TEST.ERB').write_text('''@CHECK_TITLE_BRANDING
IF GAMEBASE_TITLE != "Era_AI" || GAMEBASE_VERSIONNAME != "0.3.0"
    THROW 名称或版本元数据不正确
ENDIF
CALL CHECK_BRANDING_TEXT
CALL TITLE_ABOUT
CALL CHECK_BRANDING_TEXT
CALL CHECK_ABOUT_COLUMNS
SAVETEXT "PASS branding", "branding_pass.txt", 0, 1
RETURN

@CHECK_BRANDING_TEXT
LOCAL:0 = 0
FOR LOCAL:1, 0, LINECOUNT
    LOCALS:0 '= GETDISPLAYLINE(LOCAL:1)
    IF STRFIND(LOCALS:0, "原生整合版") >= 0 || STRFIND(LOCALS:0, "版本 1.0") >= 0 || STRFIND(LOCALS:0, "Alpha") >= 0 || STRFIND(LOCALS:0, "开发测试") >= 0
        THROW 残留旧名称或版本
    ENDIF
    IF STRFIND(LOCALS:0, "版本：0.3.0") >= 0
        LOCAL:0 += 1
    ENDIF
NEXT
IF LOCAL:0 != 1
    THROW 页面未显示统一版本
ENDIF
RETURN

@CHECK_ABOUT_COLUMNS
LOCAL:10 = -1
LOCAL:11 = 0
FOR LOCAL:1, 0, LINECOUNT
    LOCALS:0 '= GETDISPLAYLINE(LOCAL:1)
    LOCAL:2 = -1
    IF STRFIND(LOCALS:0, "剧本与进度：") == 0
        LOCAL:2 = STRFIND(LOCALS:0, "独立加载")
    ELSEIF STRFIND(LOCALS:0, "人物与物品：") == 0
        LOCAL:2 = STRFIND(LOCALS:0, "统一管理")
    ELSEIF STRFIND(LOCALS:0, "地图与交互：") == 0
        LOCAL:2 = STRFIND(LOCALS:0, "原生三维")
    ELSEIF STRFIND(LOCALS:0, "对话与记忆：") == 0
        LOCAL:2 = STRFIND(LOCALS:0, "保留历史")
    ELSEIF STRFIND(LOCALS:0, "连接与显示：") == 0
        LOCAL:2 = STRFIND(LOCALS:0, "网络 API")
    ELSEIF STRFIND(LOCALS:0, "用量记录：") == 0
        LOCAL:2 = STRFIND(LOCALS:0, "查看近7日")
    ENDIF
    IF LOCAL:2 >= 0
        LOCAL:3 = HTML_STRINGLEN(SUBSTRING(LOCALS:0, 0, LOCAL:2), 1)
        IF LOCAL:10 >= 0 && LOCAL:10 != LOCAL:3
            THROW 关于页功能正文没有对齐
        ENDIF
        LOCAL:10 = LOCAL:3
        LOCAL:11 += 1
    ENDIF
    IF STRFIND(LOCALS:0, "伤病") >= 0 || STRFIND(LOCALS:0, "疲劳判定") >= 0
        THROW 关于页仍含具体玩法示例
    ENDIF
NEXT
IF LOCAL:11 != 6
    THROW 未找到六行关于页功能介绍
ENDIF
RETURN
''',encoding='utf-8-sig')
marker=stage/'branding_pass.txt'
proc=subprocess.Popen([str((stage/'EraAI.exe').resolve())])
try:
    deadline=time.monotonic()+25
    while proc.poll() is None and not marker.exists() and time.monotonic()<deadline:time.sleep(.2)
    if not marker.exists():
        for log in stage.glob('*.log'): print(log.read_text(encoding='utf-8-sig',errors='replace')[-5000:])
    assert marker.exists(),'Branding test failed'
finally:
    if proc.poll() is None:proc.terminate()
print('PASS: main menu and about show Era_AI 0.3.0 without Alpha/development-test labels.')
