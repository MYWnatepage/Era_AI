"""Exercise the real item-picker/input ERB with a loopback API, never player credentials."""
import sys
sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
from test_support import structured_context, TEST_ROOT, GAME_DIR
from http.server import HTTPServer, BaseHTTPRequestHandler
import json
import re
import shutil
import subprocess
import threading
import time

requests = []
server_errors = []
custom_input = '我把手里的东西摆好，看看眼前的安排。'
preset_input = '抬头看看窗外的云。'
unselected_input = '安静听一会儿屋外的声音。'


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        try:
            request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            requests.append(request)
            context = structured_context(request)
            envelope = json.loads(next(message['content'] for message in reversed(request['messages'])
                                       if message['role'] == 'user'))
            index = len(requests)
            selected = context.get('selected_items', [])
            if index == 1:
                assert envelope['player_input'] == custom_input
                assert {item['id'] for item in selected} == {1, 2}
                assert {item['name'] for item in selected} == {'铜钥匙', '食材'}
                assert {item['id']: item['selected_quantity'] for item in selected} == {1: 2, 2: 1}
                for item in selected:
                    assert set(item) == {'id', 'name', 'description', 'attributes', 'type', 'amounts', 'selected_quantity'}
                    assert item['description'] and item['amounts']['player'] > 0
                assert not ({1, 2} & {item['id'] for item in context['inventory']['items']}), 'selected cards duplicated'
                seed = json.loads((stage / 'composer-seed.txt').read_text(encoding='utf-8-sig'))
                assert context['base_revision'] == seed['revision']
            elif index == 2:
                assert envelope['player_input'] == preset_input
                assert [item['id'] for item in selected] == [1]
                assert all(item['id'] != 1 for item in context['inventory']['items'])
            elif index == 3:
                assert envelope['player_input'] == unselected_input
                assert selected == [], 'previous picker selection leaked into the next request'
            elif index == 4:
                assert envelope['player_input'] == '1', 'free input must preserve literal numeric text'
                assert [item['id'] for item in selected] == [1]
                assert selected[0]['selected_quantity'] == 1
            else:
                raise AssertionError(f'unexpected request {index}, picker cancel/clear must not submit')
            content = dict(story='你暂时停下动作，仔细观察周围，没有使用或转交任何物品。',
                           turn_id=context['turn_id'], base_revision=context['base_revision'],
                           inventory_ops=[], state_ops=[], suggestions=[preset_input], effort='none')
            body = json.dumps(dict(choices=[dict(message=dict(content=json.dumps(content, ensure_ascii=False)),
                                                finish_reason='stop')])).encode()
            self.send_response(200)
            self.end_headers()
            self.wfile.write(body)
        except Exception as error:
            server_errors.append(repr(error))
            self.send_response(500)
            self.end_headers()
            self.wfile.write(b'{"error":{"message":"mock assertion failed"}}')


stage = TEST_ROOT / 'action-composer-integration'
shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns(
    '配置', '数据', '*.sav', '*.log', 'profile', 'setting.json', 'emuera.config'))
(stage / '配置').mkdir()
(stage / '配置/music-settings.json').write_text(json.dumps(dict(Enabled=False, Volume=50)), encoding='utf-8')
server = HTTPServer(('127.0.0.1', 0), Handler)
thread = threading.Thread(target=server.serve_forever, daemon=True)
thread.start()


def patch(path, before, after):
    text = path.read_text(encoding='utf-8-sig')
    assert before in text, f'test hook missing: {path.name}'
    path.write_text(text.replace(before, after, 1), encoding='utf-8-sig')


folder = stage / 'ERB/script/艾拉的奇幻冒险'
patch(stage / 'ERB/SYSTEM_TITLE.ERB', '@SYSTEM_TITLE\n', '@SYSTEM_TITLE\nCALL ACTION_COMPOSER_TEST\nQUIT\n')
patch(folder / '行动输入.ERB', '\nINPUTS\n', '\nCALL ACTION_COMPOSER_STRING\n')
patch(folder / '流程.ERB', '$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nINPUT',
      '$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nRESULT = 0')
# Failures must return to the driver rather than wait forever for an interactive key.
for name in ('流程.ERB', '行动输入.ERB'):
    path = folder / name
    path.write_text(re.sub(r'(?m)^\s*WAIT\s*$', '    ; automated caller has no pause', path.read_text(encoding='utf-8-sig')),
                    encoding='utf-8-sig')

commands = ['@item:1', '@adjust:1:1', '@item:2', custom_input,
            '@item:1', '@preset:abc', '@preset:0abc', '@preset:', '@use:abc', '@use:11',
            '@use:1', '@item:11', '@use:11', '@clear', '@item:2', '',
            '@item:1', '1', '@free', unselected_input, '@item:1', '3', '1']
cases = '\n'.join(f'    CASE {index + 1}\n        RESULTS:0 \'= "{command}"'
                  for index, command in enumerate(commands))
(stage / 'ERB/ACTION_COMPOSER_TEST.ERB').write_text(f'''@ACTION_COMPOSER_TEST
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL INIT_DISPLAY_SETTINGS
CALL RAIN_ROOM_ENSURE_FOOD
AICONNECTION 2
RESULTS:0 '= "http://127.0.0.1:{server.server_port}/v1"
AICONNECTION 3
RESULTS:0 '= "mock-action-composer"
AICONNECTION 4
RESULTS:0 '= "fake-loopback-composer-key"
AICONNECTION 5
CALL NEW_RAIN_ROOM_AI_HISTORY
SAVESTR:90 '= SAVESTR:61
GLOBAL:831 = TIME
SAVETEXT SAVESTR:61, "composer-seed.txt", 0, 1
CALL RAIN_ROOM_COMPOSE
IF TIME != 1
    THROW selecting multiple items and custom input did not adopt one turn
ENDIF
SAVETEXT SAVESTR:61, "composer-custom.txt", 0, 1
SAVESTR:90 '= SAVESTR:61
GLOBAL:831 = TIME
CALL RAIN_ROOM_COMPOSE
IF SAVESTR:61 != SAVESTR:90 || TIME != 1
    THROW clearing or cancelling picker changed snapshot or time
ENDIF
SAVETEXT SAVESTR:61, "composer-cancel.txt", 0, 1
CALL RAIN_ROOM_COMPOSE
IF TIME != 2
    THROW preset input did not adopt exactly one turn
ENDIF
SAVESTR:90 '= SAVESTR:61
GLOBAL:831 = TIME
CALL RAIN_ROOM_COMPOSE
IF TIME != 3
    THROW free entry did not send exactly one completed turn
ENDIF
SAVESTR:90 '= SAVESTR:61
GLOBAL:831 = TIME
CALL RAIN_ROOM_COMPOSE
IF TIME != 4 || GLOBAL:830 != {len(commands)}
    THROW unexpected composer inputs or time advancement
ENDIF
SAVETEXT SAVESTR:61, "composer-final.txt", 0, 1
SAVETEXT "PASS action composer", "composer-pass.txt", 0, 1
QUIT

@ACTION_COMPOSER_STRING
IF SAVESTR:61 != SAVESTR:90 || TIME != GLOBAL:831
    THROW picker changed authoritative state before request submission
ENDIF
GLOBAL:830 += 1
SELECTCASE GLOBAL:830
{cases}
    CASEELSE
        THROW unexpected extra composer input
ENDSELECT
RETURN
''', encoding='utf-8-sig')

proc = subprocess.Popen([str(stage / 'EraAI.exe')])
try:
    marker = stage / 'composer-pass.txt'
    deadline = time.monotonic() + 55
    while proc.poll() is None and not marker.exists() and time.monotonic() < deadline:
        time.sleep(.05)
    if not marker.exists():
        for log in stage.glob('*.log'):
            print(log.read_text(encoding='utf-8-sig', errors='replace')[-9000:])
        print('mock errors:', server_errors)
        print('requests completed:', len(requests))
    assert not server_errors, server_errors
    assert marker.exists(), 'actual item-picker/custom/preset composer did not finish'
    assert len(requests) == 4, f'cancel/clear sent requests: {len(requests)}'
    read = lambda name: json.loads((stage / name).read_text(encoding='utf-8-sig'))
    initial = read('composer-seed.txt')
    custom = read('composer-custom.txt')
    cancelled = read('composer-cancel.txt')
    final = read('composer-final.txt')
    assert custom == cancelled, 'cancel mutated the authoritative snapshot'
    assert initial['items'] == custom['items'] == final['items'], 'context selection consumed/transferred items'
    assert initial['statuses'] == final['statuses'], 'context selection changed character statuses'
    history = json.loads((stage / '数据/记忆/rain_room.json').read_text(encoding='utf-8-sig'))
    assert [turn['Player'] for turn in history] == [custom_input, preset_input, unselected_input, '1']
    print('PASS actual composer: multi-select full cards, raw/preset input, malformed preset/use commands rejected, no duplicate inventory, clear/cancel atomicity and no selection leak', flush=True)
finally:
    if proc.poll() is None:
        proc.terminate()
    proc.wait(timeout=10)
    server.shutdown()
    thread.join(timeout=2)
