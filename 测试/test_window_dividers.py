from test_support import TEST_ROOT, GAME_DIR
import shutil,subprocess,time

stage=TEST_ROOT/'window-dividers'
shutil.copytree(GAME_DIR,stage,ignore=shutil.ignore_patterns('配置','数据','*.sav','*.log','profile','setting.json','emuera.config'))
p=stage/'ERB/SYSTEM_TITLE.ERB'
p.write_text(p.read_text(encoding='utf-8-sig').replace('@SYSTEM_TITLE\n','@SYSTEM_TITLE\nCALL CHECK_WINDOW_DIVIDERS\nQUIT\n',1),encoding='utf-8-sig')
(stage/'ERB/DIVIDER_TEST.ERB').write_text('''@CHECK_WINDOW_DIVIDERS
CALL INIT_DISPLAY_SETTINGS
CALL APPLY_DISPLAY_SETTINGS
FOR LOCAL:10, 0, 8
    SELECTCASE LOCAL:10
        CASE 0, 5
            RESULTS:0 '= "1024 640"
        CASE 1
            RESULTS:0 '= "1600 900"
        CASE 2
            RESULTS:0 '= "1280 800"
        CASE 3
            RESULTS:0 '= "7680 4320"
        CASE 4
            DISPLAYTYPE 7
        CASE 6
            RESULT = 36
            DISPLAYTYPE 2
            RESULTS:0 '= "1280 720"
        CASE 7
            RESULT = 18
            DISPLAYTYPE 2
            RESULTS:0 '= "1024 640"
    ENDSELECT
    IF LOCAL:10 != 4
        DISPLAYTYPE 6
    ENDIF
    CLEARLINE LINECOUNT
    DRAWLINE
    CALL CHECK_ONE_DIVIDER, "default"
    CLEARLINE LINECOUNT
    CUSTOMDRAWLINE -
    CALL CHECK_ONE_DIVIDER, "custom"
    CLEARLINE LINECOUNT
    DRAWLINEFORM -
    CALL CHECK_ONE_DIVIDER, "form"
    CLEARLINE LINECOUNT
    PRINTFORML %DRAWLINESTR%
    CALL CHECK_ONE_DIVIDER, "variable"
NEXT
SAVETEXT "PASS", "divider-result.txt", 0, 1
QUIT

@CHECK_ONE_DIVIDER, ARGS
IF LINECOUNT != 1
    SAVETEXT STRFORM("FAIL %ARGS% wrap at step {LOCAL:10}: {LINECOUNT} lines"), "divider-result.txt", 0, 1
    QUIT
ENDIF
LOCALS:0 '= GETDISPLAYLINE(0)
LOCAL:0 = HTML_STRINGLEN(HTML_ESCAPE(LOCALS:0), 1)
LOCAL:1 = HTML_STRINGLEN(HTML_ESCAPE(LOCALS:0 + "-"), 1)
IF LOCAL:0 > CLIENTWIDTH() || CLIENTWIDTH() - LOCAL:0 > HTML_STRINGLEN("-", 1) + 16
    SAVETEXT STRFORM("FAIL %ARGS% width {LOCAL:0}, viewport {CLIENTWIDTH()}"), "divider-result.txt", 0, 1
    QUIT
ENDIF
RETURN
''',encoding='utf-8-sig')
marker=stage/'divider-result.txt'
proc=subprocess.Popen([str((stage/'EraAI.exe').resolve())])
try:
    deadline=time.monotonic()+30
    while proc.poll() is None and not marker.exists() and time.monotonic()<deadline:time.sleep(.1)
    assert marker.exists(),'Divider test did not complete'
    result=marker.read_text(encoding='utf-8-sig').strip()
    assert result=='PASS',result
finally:
    if proc.poll() is None:proc.terminate();proc.wait(timeout=5)
print('PASS: single-line, near-full-width dividers after shrink/grow, maximize and font changes; all divider variants.')
