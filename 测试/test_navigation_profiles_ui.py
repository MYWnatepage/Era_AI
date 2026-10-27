"""Exercise actual key handlers and ERB pages without calling any API or touching user saves."""
from test_support import TEST_ROOT, PROJECT_ROOT, GAME_DIR
import json, shutil, subprocess

stage = TEST_ROOT / 'navigation-profiles-ui'
shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns('配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
(stage / '配置').mkdir()
(stage / '配置/music-settings.json').write_text(json.dumps(dict(Enabled=False, Volume=50)), encoding='utf-8')
assembly = PROJECT_ROOT / '源码/Emuera/artifacts/bin/Emuera/release-naudio_win-x64/EraAI.dll'
subprocess.run(['dotnet', 'run', '--no-build', '--project', str(PROJECT_ROOT / '测试/NativeAI.Tests.csproj'),
                '-c', 'Release', '--', '--navigation-profile-ui', str(assembly), str(stage)], check=True, timeout=50)
assert not (stage / '数据/账户/api-usage.json').exists(), 'UI test must not call a paid API'
print('PASS: Chinese folder picker, repeated Esc and zero, character profiles, scene return label; no API.', flush=True)
