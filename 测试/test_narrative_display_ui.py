"""Real settings slider and ERB narrative presentation; no API or credentials."""
from test_support import TEST_ROOT, PROJECT_ROOT, GAME_DIR
import json, shutil, subprocess

assembly = PROJECT_ROOT / '源码/Emuera/artifacts/bin/Emuera/release-naudio_win-x64/EraAI.dll'
for mode in ['gradual', 'review', 'instant', 'natural']:
    stage = TEST_ROOT / ('narrative-display-' + mode)
    shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns('配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config', '.era-ai-session.lock'))
    (stage / '配置').mkdir()
    (stage / '配置/music-settings.json').write_text(json.dumps(dict(Enabled=False, Volume=50)), encoding='utf-8')
    (stage / 'narrative-isolated.txt').write_text('Isolated narrative display fixture.', encoding='utf-8')
    subprocess.run(['dotnet', 'run', '--no-build', '--project', str(PROJECT_ROOT / '测试/NativeAI.Tests.csproj'), '-c', 'Release', '--',
                    '--narrative-display-ui', str(assembly), str(stage), mode], check=True, timeout=60)
    assert not (stage / '数据/账户/api-usage.json').exists()
print('PASS actual settings slider, gradual/instant stories, proposal review, skipping, wrapping and unchanged saves; no API.', flush=True)
