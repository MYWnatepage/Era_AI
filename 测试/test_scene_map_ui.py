"""Exercise map paint, hover, toolbar overlay and return through the real player in an isolated copy."""
from test_support import TEST_ROOT, PROJECT_ROOT, GAME_DIR
import json, os, shutil, subprocess

stage = TEST_ROOT / 'scene-map-ui'
shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns('配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
(stage / '配置').mkdir()
(stage / '配置/music-settings.json').write_text(json.dumps(dict(Enabled=False, Volume=50)), encoding='utf-8')
(stage / '配置/typography-settings.json').write_text(json.dumps(dict(FontName='Microsoft YaHei UI', FontSize=24, LineHeight=32)), encoding='utf-8')
(stage / '配置/display-settings.json').write_text(json.dumps(dict(TextRgb=0xDCDCDC, BackgroundRgb=0x0F0F12, FocusRgb=0xFFFF00)), encoding='utf-8')
os.environ['ERA_SCENE_MAP_PREVIEW'] = str(PROJECT_ROOT.parent / 'diagnostics' / 'scene-web')
p = stage / 'ERB/script/艾拉的奇幻冒险/物品行动.ERB'
s = p.read_text(encoding='utf-8-sig')
s = s.replace('@RAIN_ROOM_WORLD_PAGE\n', '''@RAIN_ROOM_WORLD_PAGE
SAVETEXT SAVESTR:61, "map-before.txt", 0, 1
SAVESTR:90 '= SAVESTR:61
FLAG:90 = TIME
''', 1)
s = s.replace('AICONNECTION 97\n', 'AICONNECTION 97\nSAVETEXT SAVESTR:61, "map-current.txt", 0, 1\n', 1)
needle = 'ESCAPE 0\nINPUT\nRETURN'
pos = s.index(needle, s.index('@RAIN_ROOM_WORLD_PAGE'))
s = s[:pos] + s[pos:].replace(needle, '''ESCAPE 0
INPUT
IF SAVESTR:90 != SAVESTR:61 || FLAG:90 != TIME
    THROW map navigation changed progress
ENDIF
GLOBAL:890 += 1
SAVETEXT STRFORM("{GLOBAL:890}"), "map-returns.txt", 0, 1
RETURN''', 1)
p.write_text(s, encoding='utf-8-sig')
assembly = os.environ.get('ERA_SCENE_MAP_ASSEMBLY') or str(PROJECT_ROOT / '源码/Emuera/artifacts/bin/Emuera/release-naudio_win-x64/EraAI.dll')
mode = os.environ.get('ERA_SCENE_MAP_MODE', 'native')
route = '--scene-map-web-ui' if mode == 'web' else '--scene-map-native-ui'
try:
    subprocess.run(['dotnet', 'run', '--no-build', '--project', str(PROJECT_ROOT / '测试/NativeAI.Tests.csproj'),
                    '-c', 'Release', '--', route, str(assembly), str(stage)], check=True, timeout=100)
except subprocess.CalledProcessError:
    diagnostic = stage / '数据/地图缓存/map-host.log'
    if diagnostic.exists():
        print(diagnostic.read_text(encoding='utf-8'), flush=True)
    raise
assert not (stage / '数据/账户/api-usage.json').exists(), 'Map browsing must never call an API'
print('PASS: ' + mode + ' 3D map, live save data, fixed layouts, interaction, theme, chart overlay, zero/Esc return; no API.', flush=True)
