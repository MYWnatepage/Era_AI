"""Real native HUD slots at fractional AP, fatigue penalty and two display themes; no API."""
from test_support import TEST_ROOT, PROJECT_ROOT, GAME_DIR
import json
import shutil
import subprocess

stage = TEST_ROOT / 'resource-bars-ui'
shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns(
    '配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
(stage / '配置').mkdir()
(stage / '配置/music-settings.json').write_text(json.dumps(dict(Enabled=False, Volume=50)), encoding='utf-8')
(stage / 'resource-bars-ui-isolated.txt').write_text('Marked isolated fixture for native resource HUD checks.', encoding='utf-8')
assembly = PROJECT_ROOT / '源码/Emuera/artifacts/bin/Emuera/release-naudio_win-x64/EraAI.dll'
subprocess.run(['dotnet', 'run', '--no-build', '--project', str(PROJECT_ROOT / '测试/NativeAI.Tests.csproj'),
                '-c', 'Release', '--', '--resource-bars-ui', str(assembly), str(stage)], check=True, timeout=65)
assert not (stage / '数据/账户/api-usage.json').exists(), 'Resource HUD test must not call an API'
print('PASS actual resource HUD fractional slots, colours, fonts, compact width and unchanged state; no API.', flush=True)
