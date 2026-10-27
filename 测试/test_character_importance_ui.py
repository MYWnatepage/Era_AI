"""Real character importance menus and proposal read-only view in an isolated copy; no API."""
from test_support import TEST_ROOT, PROJECT_ROOT, GAME_DIR
import json
import shutil
import subprocess

stage = TEST_ROOT / 'character-importance-ui'
shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns(
    '配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
(stage / '配置').mkdir()
(stage / '配置/music-settings.json').write_text(json.dumps(dict(Enabled=False, Volume=50)), encoding='utf-8')
(stage / 'importance-ui-isolated.txt').write_text('Marked isolated fixture; safe for test-only ERB hooks.', encoding='utf-8')
assembly = PROJECT_ROOT / '源码/Emuera/artifacts/bin/Emuera/release-naudio_win-x64/EraAI.dll'
subprocess.run(['dotnet', 'run', '--no-build', '--project', str(PROJECT_ROOT / '测试/NativeAI.Tests.csproj'),
                '-c', 'Release', '--', '--character-importance-ui', str(assembly), str(stage)], check=True, timeout=65)
assert not (stage / '数据/账户/api-usage.json').exists(), 'Importance UI test must not call an API'
print('PASS actual major/minor display, confirmed upgrade/downgrade, cancellation, preserved facts and read-only review; no API.', flush=True)
