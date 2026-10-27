from test_support import TEST_ROOT, GAME_DIR
import json, shutil, subprocess, time
import sys
sys.stdout.reconfigure(encoding='utf-8',errors='backslashreplace')

stage=TEST_ROOT/'framework-layout'
shutil.copytree(GAME_DIR,stage,ignore=shutil.ignore_patterns('script','配置','数据','*.sav','*.log','profile','setting.json','emuera.config'))
(stage/'配置').mkdir()
(stage/'配置/music-settings.json').write_text(json.dumps(dict(Enabled=False,Volume=50)),encoding='utf-8')
p=stage/'ERB/SYSTEM_TITLE.ERB'
p.write_text(p.read_text(encoding='utf-8-sig').replace('@SYSTEM_TITLE\n','@SYSTEM_TITLE\nCALL FRAMEWORK_LAYOUT_TEST\nQUIT\n',1),encoding='utf-8-sig')
# Render actual pages and return at their normal input point without selecting actions.
for name in ['SYSTEM_SETTINGS.ERB','SYSTEM_GENERATION.ERB','SYSTEM_DISPLAY.ERB','SYSTEM_MUSIC.ERB','SYSTEM_MODEL.ERB','SYSTEM_ABOUT.ERB']:
    p=stage/'ERB'/name
    s=p.read_text(encoding='utf-8-sig').replace('\nINPUT\n','\nRESULT = 0\n')
    p.write_text(s,encoding='utf-8-sig')
p=stage/'ERB/SYSTEM_HELP.ERB'
s=p.read_text(encoding='utf-8-sig').replace('\nINPUT\n','\nCALL CHECK_HELP_CONTENT, LOCAL:0\nGLOBAL:980 += 1\nIF GLOBAL:980 < 3\n    RESULT = 1\nELSE\n    RESULT = 0\nENDIF\n',1)
p.write_text(s,encoding='utf-8-sig')
fixture='''@FRAMEWORK_LAYOUT_TEST
CALL INIT_DISPLAY_SETTINGS
CALL APPLY_DISPLAY_SETTINGS
FOR LOCAL:20, 0, 4
    SELECTCASE LOCAL:20
        CASE 0
            RESULTS:0 '= "640 640"
            RESULT = 24
        CASE 1
            RESULTS:0 '= "640 640"
            RESULT = 48
        CASE 2
            RESULTS:0 '= "1024 720"
            RESULT = 36
        CASE 3
            RESULTS:0 '= "1600 900"
            RESULT = 24
    ENDSELECT
    LOCALS:20 '= RESULTS:0
    DISPLAYTYPE 2
    RESULTS:0 '= LOCALS:20
    DISPLAYTYPE 6
    CALL APPLY_DISPLAY_SETTINGS
    CLEARLINE LINECOUNT
    RESULT = -123
    RESULTS:29 '= "layout sentinel"
    CALL SETTING_ROW, "[5] 示例参数", "这是包含 EnglishWords 和 emoji 🎲 的长说明，用来检查换行后没有遗失内容，且不会拆开完整字符。", 200
    IF RESULT != -123 || RESULTS:29 != "layout sentinel"
        THROW Layout changed result registers
    ENDIF
    CALL CHECK_LAYOUT_WIDTH
    LOCALS:21 '= ""
    FOR LOCAL:21, 0, LINECOUNT
        LOCALS:21 += HTML_TOPLAINTEXT(GETDISPLAYLINE(LOCAL:21))
    NEXT
    IF STRFIND(LOCALS:21, "🎲") < 0 || STRFIND(LOCALS:21, "不会拆开完整字符。") < 0
        THROW Layout lost Unicode content
    ENDIF
    CALL TITLE_SETTING
    CALL CHECK_LAYOUT_WIDTH
    CALL GENERATION_SAMPLING_PAGE
    CALL CHECK_LAYOUT_WIDTH
    CALL CHECK_PRESET_TEXT
    CALL GENERATION_LENGTH_PAGE
    CALL CHECK_LAYOUT_WIDTH
    CALL GENERATION_CONTEXT_PAGE
    CALL CHECK_LAYOUT_WIDTH
    CALL GENERATION_THINKING_PAGE
    CALL CHECK_LAYOUT_WIDTH
    CALL TITLE_SETTING_WINDOW_SIZE
    CALL CHECK_LAYOUT_WIDTH
    CALL TITLE_SETTING_MUSIC
    CALL CHECK_LAYOUT_WIDTH
    CALL TITLE_SETTING_AI_MODEL_INFO
    CALL CHECK_LAYOUT_WIDTH
    GLOBAL:980 = 0
    CALL TITLE_HELP
    CALL TITLE_ABOUT
    CALL CHECK_LAYOUT_WIDTH
NEXT
SAVETEXT "PASS framework layout", "layout-pass.txt", 0, 1
QUIT

@CHECK_LAYOUT_WIDTH
FOR LOCAL:0, 0, LINECOUNT
    LOCALS:0 '= GETDISPLAYLINE(LOCAL:0)
    IF HTML_STRINGLEN(LOCALS:0, 1) > CLIENTWIDTH()
        SAVETEXT LOCALS:0, "overwide-line.txt", 0, 1
        THROW Page contains an overwide physical line
    ENDIF
NEXT
RETURN

@CHECK_PRESET_TEXT
LOCALS:0 '= ""
FOR LOCAL:0, 0, LINECOUNT
    LOCALS:0 += HTML_TOPLAINTEXT(GETDISPLAYLINE(LOCAL:0))
NEXT
IF STRFIND(LOCALS:0, "随机程度 0.8") < 0 || STRFIND(LOCALS:0, "随机程度 0.4") < 0 || STRFIND(LOCALS:0, "随机程度 1.1") < 0 || STRFIND(LOCALS:0, "话题扩展 0.1") < 0
    THROW Preset rows lost parameter values
ENDIF
RETURN

@CHECK_HELP_CONTENT, ARG
CALL CHECK_LAYOUT_WIDTH
LOCALS:0 '= ""
FOR LOCAL:0, 0, LINECOUNT
    LOCALS:0 += HTML_TOPLAINTEXT(GETDISPLAYLINE(LOCAL:0))
NEXT
IF ARG == 0
    IF STRFIND(LOCALS:0, "首次使用") < 0 || STRFIND(LOCALS:0, "API 密钥") < 0 || STRFIND(LOCALS:0, "Esc") < 0 || STRFIND(LOCALS:0, "保存进度") < 0
        THROW Missing basic help content
    ENDIF
ELSE
    IF STRFIND(LOCALS:0, "Windows 10 / 11") < 0 || STRFIND(LOCALS:0, "[未完成]") < 0
        SAVETEXT LOCALS:0, "help-missing.txt", 0, 1
        THROW Existing requirements missing
    ENDIF
ENDIF
IF STRFIND(LOCALS:0, "艾拉") >= 0 || STRFIND(LOCALS:0, "狩猎") >= 0
    THROW Framework help coupled to scenario
ENDIF
RETURN
'''
(stage/'ERB/LAYOUT_TEST.ERB').write_text(fixture,encoding='utf-8-sig')
marker=stage/'layout-pass.txt'
proc=subprocess.Popen([str(stage/'EraAI.exe')])
try:
    deadline=time.monotonic()+45
    while proc.poll() is None and not marker.exists() and time.monotonic()<deadline:time.sleep(.1)
    if not marker.exists():
        for log in stage.glob('*.log'):print(log.read_text(encoding='utf-8-sig',errors='replace')[-6000:])
        if (stage/'overwide-line.txt').exists():print((stage/'overwide-line.txt').read_text(encoding='utf-8-sig'))
        if (stage/'help-missing.txt').exists():print('HELP TEXT:',(stage/'help-missing.txt').read_text(encoding='utf-8-sig'))
    assert marker.exists(),'Framework layout/help test failed'
finally:
    if proc.poll() is None:proc.terminate()
    proc.wait(timeout=5)
print('PASS: responsive framework pages at 640-1600px, 24-48px fonts, Unicode, untouched registers and help navigation without a scenario.',flush=True)
