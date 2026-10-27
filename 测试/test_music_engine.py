from test_support import TEST_ROOT, GAME_DIR
from pathlib import Path
import json, shutil, subprocess, time, wave

stage = TEST_ROOT / 'music-engine'
shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns('配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
(stage / '资源/音乐/Celtic Impulse.mp3').unlink(missing_ok=True)
(stage / '配置').mkdir(exist_ok=True)
settings = stage / '配置/music-settings.json'
settings.write_text(json.dumps({'Enabled': False, 'Volume': 50, 'CustomPath': ''}), encoding='utf-8')
custom = stage / '自定义音乐.wav'
with wave.open(str(custom), 'wb') as audio:
    audio.setnchannels(1); audio.setsampwidth(2); audio.setframerate(44100)
    audio.writeframes(b'\0\0' * 4410)
(stage / 'broken.mp3').write_bytes(b'not an audio file')
title = stage / 'ERB/SYSTEM_TITLE.ERB'
text = title.read_text(encoding='utf-8-sig').replace('@SYSTEM_TITLE\n', '@SYSTEM_TITLE\nCALL MUSIC_TEST\nQUIT\n', 1)
title.write_text(text, encoding='utf-8-sig')
title = stage / 'ERB/SYSTEM_SETTINGS.ERB'
text = title.read_text(encoding='utf-8-sig')

a, b = text.index('@TITLE_SETTING\n'), text.index('\n@PRINT_AUTOSAVE_NAME\n')
page = text[a:b].replace('\nINPUT\n', '''
GLOBAL:975 += 1
SELECTCASE GLOBAL:975
    CASE 1
        RESULT = 9001
    CASE 2
        RESULT = 9002
    CASE 3
        RESULT = 8
    CASEELSE
        RESULT = 0
ENDSELECT
''', 1)
text = text[:a] + page + text[b:]
title.write_text(text, encoding='utf-8-sig')
music = stage / 'ERB/SYSTEM_MUSIC.ERB'
music_text = music.read_text(encoding='utf-8-sig')
menu_start = music_text.index('@TITLE_SETTING_BUILTIN_MUSIC')
music_text = music_text[:menu_start].replace('\nINPUT\n', '\nRESULT = 0\n') + music_text[menu_start:].replace('\nINPUT\n', '\nGLOBAL:976 += 1\nIF GLOBAL:976 == 1\n    RESULT = 2\nELSE\n    RESULT = 0\nENDIF\n')
music.write_text(music_text, encoding='utf-8-sig')
script = '''@MUSIC_TEST
CALL INIT_DISPLAY_SETTINGS
CALL APPLY_DISPLAY_SETTINGS
CALL TITLE_SETTING
IF GLOBAL:975 != 4
    THROW 音量快捷键或返回流程未执行
ENDIF
MUSIC 0
IF RESULTS:0 != "关" || RESULT != 50
    THROW 开关和音量默认值不正确
ENDIF
RESULT = 0
MUSIC 4
RESULTS:0 '= "自定义音乐.wav"
MUSIC 3
IF RESULT != 1
    THROW 无法选择中文路径的有效音乐
ENDIF
RESULTS:0 '= "broken.mp3"
MUSIC 3
IF RESULT != -1
    THROW 无效音频未被拦截
ENDIF
RESULTS:0 '= "missing.mp3"
MUSIC 3
IF RESULT != -1
    THROW 缺失文件未被拦截
ENDIF
MUSIC 0
IF STRFIND(RESULTS:2, "自定义音乐.wav") < 0 || RESULT != 0
    THROW 无效文件覆盖了原音乐或音量
ENDIF
MUSIC 2
MUSIC 1
MUSIC 0
IF RESULTS:0 != "开" || RESULTS:3 != ""
    THROW 内置音乐无法解码播放
ENDIF
CALL TITLE_SETTING_BUILTIN_MUSIC
MUSIC 0
IF RESULTS:4 != "1" || STRFIND(RESULTS:2, "Evening Fall") < 0 || RESULTS:3 != ""
    THROW 第二首内置曲目选择或播放失败
ENDIF
CALL TITLE_SETTING_BUILTIN_MUSIC
MUSIC 0
IF RESULTS:4 != "1"
    THROW 取消选择改变了内置音乐
ENDIF
RESULT = 2
MUSIC 11
IF RESULT != -1
    THROW 无效内置曲目未拒绝
ENDIF
RESULT = 101
MUSIC 4
IF RESULT != -1
    THROW 越界音量未拒绝
ENDIF
SAVETEXT "PASS music", "music-pass.txt", 0, 1
QUIT
'''
test = stage / 'ERB/MUSIC_TEST.ERB'
test.write_text(script, encoding='utf-8-sig')

def run(marker):
    p = subprocess.Popen([str((stage / 'EraAI.exe').resolve())])
    try:
        deadline = time.monotonic() + 30
        while p.poll() is None and not marker.exists() and time.monotonic() < deadline:
            time.sleep(.15)

        if not marker.exists():
            for log in stage.rglob('*.log'):
                print(log.name, log.read_text(encoding='utf-8-sig', errors='replace')[-6000:])
            print('process return', p.poll())
        assert marker.exists(), 'Music engine test failed: ' + marker.name
    finally:
        if p.poll() is None: p.terminate(); p.wait(timeout=5)

run(stage / 'music-pass.txt')
saved = json.loads(settings.read_text())
assert saved == {'Enabled': True, 'Volume': 0, 'CustomPath': '', 'BuiltInTrack': 1}, saved
test.write_text('''@MUSIC_TEST
CALL INIT_DISPLAY_SETTINGS
CALL APPLY_DISPLAY_SETTINGS
MUSIC 0
IF RESULTS:0 != "开" || RESULT != 0 || RESULTS:4 != "1" || STRFIND(RESULTS:2, "Evening Fall") < 0 || RESULTS:3 != ""
    THROW 重启后未恢复开关、音量或第二首内置音乐
ENDIF
MUSIC 1
SAVETEXT "PASS restart", "music-restart.txt", 0, 1
QUIT
''', encoding='utf-8-sig')
run(stage / 'music-restart.txt')
assert json.loads(settings.read_text())['Enabled'] is False
print('PASS: music menu and shifted numbering, shortcut steps, custom audio validation, embedded playback without external music file and restart persistence.')
