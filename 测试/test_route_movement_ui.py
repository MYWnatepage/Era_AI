"""Real movement-map entry, route confirmation, ERB commit, cancellation and fractional time; no API."""
from test_support import TEST_ROOT, PROJECT_ROOT, GAME_DIR
import json, shutil, subprocess

stage = TEST_ROOT / 'route-movement-ui'
shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns('配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
(stage / '配置').mkdir()
(stage / '配置/music-settings.json').write_text(json.dumps(dict(Enabled=False, Volume=50)), encoding='utf-8')
p = stage / 'ERB/script/艾拉的奇幻冒险/流程.ERB'
s = p.read_text(encoding='utf-8-sig')
s = s.replace('@SCENARIO_TEST_RAIN_ROOM_START\n', '@SCENARIO_TEST_RAIN_ROOM_START\nGLOBAL:300 = 1\n', 1)
for label in ['$SCENARIO_TEST_RAIN_ROOM_INPUT', '$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT']:
    s = s.replace(label+'\n', label+'''\nSAVETEXT SAVESTR:61, "travel-state.txt", 0, 1
SAVETEXT STRFORM("{DAY},{TIME},{FLAG:20}"), "travel-clock.txt", 0, 1
''', 1)
p.write_text(s, encoding='utf-8-sig')
assembly = PROJECT_ROOT / '源码/Emuera/artifacts/bin/Emuera/release-naudio_win-x64/EraAI.dll'
subprocess.run(['dotnet', 'run', '--no-build', '--project', str(PROJECT_ROOT / '测试/NativeAI.Tests.csproj'), '-c', 'Release', '--',
                '--route-movement-ui', str(assembly), str(stage)], check=True, timeout=75)
assert not (stage / '数据/账户/api-usage.json').exists()
print('PASS actual route movement, free saved coordinates, fractional time, cancellation and no API.', flush=True)
