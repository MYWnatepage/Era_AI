from test_support import TEST_ROOT, PROJECT_ROOT, GAME_DIR
from pathlib import Path
import json, shutil, subprocess

stage = TEST_ROOT / 'usage-shortcut'
shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns('script', '配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
(stage / '配置').mkdir()
(stage / '配置/music-settings.json').write_text(json.dumps(dict(Enabled=False, Volume=50)), encoding='utf-8')
assembly = PROJECT_ROOT / '源码/Emuera/artifacts/bin/Emuera/release-naudio_win-x64/EraAI.dll'
if not assembly.exists():
    raise RuntimeError('请先运行构建.ps1，生成用于窗口集成测试的运行器。')
subprocess.run(['dotnet', 'run', '--no-build', '--project', str(PROJECT_ROOT / '测试/NativeAI.Tests.csproj'),
                '-c', 'Release', '--', '--toolbar-ui', str(assembly), str(stage)], check=True, timeout=45)
assert not (stage / '数据/存档').exists(), 'viewing usage must not create a game save'
print('PASS: usage shortcut uses the real player with disposable local statistics and no model requests.', flush=True)
