from pathlib import Path
import atexit
import shutil
import tempfile

PROJECT_ROOT = Path(__file__).resolve().parents[1]
GAME_DIR = PROJECT_ROOT / '游戏'
GAME_EXE = GAME_DIR / 'EraAI.exe'
if not GAME_EXE.is_file():
    raise RuntimeError('请先运行构建.ps1，生成 游戏/EraAI.exe。')
TEST_ROOT = Path(tempfile.mkdtemp(prefix='era-ai-tests-'))
atexit.register(shutil.rmtree, TEST_ROOT, ignore_errors=True)
