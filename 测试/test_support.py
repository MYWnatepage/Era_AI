from pathlib import Path
import atexit
import shutil
import tempfile
import json
import re

def structured_context(request):
    """Read the actual turn envelope; keep compatibility for legacy non-inventory probes."""
    for message in reversed(request['messages']):
        if message['role'] != 'user':
            continue
        try:
            payload = json.loads(message['content'])
        except (ValueError, TypeError):
            break
        if isinstance(payload, dict) and 'program_context' in payload:
            return payload['program_context']
        break
    system = request['messages'][0]['content']
    turn = re.search(r'"turn_id":"([a-zA-Z0-9_-]+)"', system)
    revision = re.search(r'"base_revision":(\d+)', system)
    if turn is None or revision is None:
        raise AssertionError('structured request has no turn identity')
    return dict(turn_id=turn.group(1), base_revision=int(revision.group(1)), legacy_system=system)

PROJECT_ROOT = Path(__file__).resolve().parents[1]
GAME_DIR = PROJECT_ROOT / '游戏'
GAME_EXE = GAME_DIR / 'EraAI.exe'
if not GAME_EXE.is_file():
    raise RuntimeError('请先运行构建.ps1，生成 游戏/EraAI.exe。')
TEST_ROOT = Path(tempfile.mkdtemp(prefix='era-ai-tests-'))
atexit.register(shutil.rmtree, TEST_ROOT, ignore_errors=True)
