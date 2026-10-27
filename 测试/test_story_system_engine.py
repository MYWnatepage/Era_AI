"""Actual ERB/native integration, loopback mock only; never reads the player's API key."""
import sys
sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
from test_support import structured_context, TEST_ROOT, GAME_DIR
from http.server import HTTPServer, BaseHTTPRequestHandler
import json
import shutil
import subprocess
import threading
import time

requests = []
server_errors = []


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        try:
            request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            requests.append(request)
            context = structured_context(request)
            index = len(requests)
            content = dict(story='你停下脚步，仔细考虑眼前的安排，没有发生其他变化。',
                           turn_id=context['turn_id'], base_revision=context['base_revision'],
                           inventory_ops=[], state_ops=[], suggestions=[], effort='none')
            if index == 1:
                content['story'] = '整理柜子时，你找到两条干净布条；伸手取出时左手指被木刺浅浅划伤，尚未处理。'
                content['inventory_ops'] = [dict(op='create_item', name='干净布条', description='干净的亚麻布条，可以用于包扎浅伤。',
                                                 type='消耗品', attributes='材质：亚麻；用途：包扎', quantity=2,
                                                 owner='player', reason='本轮整理柜子找到')]
                content['state_ops'] = [dict(op='create_status', character='player', name='左手指浅划伤', category='伤势',
                                             severity='轻微', description='左手指有浅划伤，尚未处理。', effects='按压时疼痛',
                                             reason='本轮取布时被木刺划伤')]
            if index == 2:
                assert context['selected_item']['mode'] == 'context'
                chosen = context['selected_item']
                # Full local card must remain available even when input does not name the item.
                assert '干净的亚麻布条' in json.dumps(chosen, ensure_ascii=False)
                assert context['selected_item']['cost'] == 0
                content['story'] = '你检查手中材料的质地，纤维干净且柔软，只查看并没有使用。'
            if index in (3, 4, 5, 6):
                plan = context['selected_item']
                assert plan['mode'] == 'bandage' and plan['cost'] == 1 and plan['status_id'] == 1
                assert '未痊愈' in plan['description']
                content['story'] = '你用选中的材料包好左手指，伤口仍需休养，手指尚未痊愈。'
            if index == 5:
                content['inventory_ops'] = [dict(op='change_quantity', item_id=12, owner='player', delta=-1,
                                                 reason='不得重复扣除程序包扎材料')]
            if index == 7:
                content['story'] = '你想起与艾拉谈起的集市安排，答应下次出行先一起列好清单，这个约定尚未履行。'
                content['memory_proposals'] = [dict(summary='主角答应下次去集市前与艾拉列好清单，尚未履行。', keywords=['集市约定'])]
            if index in (8, 9, 10):
                memories = context['long_term_memories']
                if index == 9:
                    assert len(memories) == 1 and '列好清单' in memories[0]['summary']
                else:
                    assert memories == [], f'pending or unrelated memory leaked at request {index}'
                content['story'] = '你继续思考下一步安排，没有消耗物品，也没有改变伤势。'
            if index == 11:
                content['story'] = '你独自走到溪边，艾拉已经在村落集市等候，手指的包扎仍在。'
                content['scene_update'] = dict(location='溪边', present_characters=['player'], summary='主角独自在溪边。',
                                               character_locations=dict(player='溪边', ella='村落集市'),
                                               reason='本轮确认分开行动，主角到溪边而艾拉在集市')
            answer = '{"story":' if index == 4 else json.dumps(content, ensure_ascii=False)
            body = json.dumps(dict(choices=[dict(message=dict(content=answer), finish_reason='stop')])).encode()
            self.send_response(200)
            self.end_headers()
            self.wfile.write(body)
        except Exception as error:
            server_errors.append(repr(error))
            self.send_response(500)
            self.end_headers()
            self.wfile.write(b'{"error":{"message":"mock assertion failed"}}')


stage = TEST_ROOT / 'story-system-integration'
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
patch(stage / 'ERB/SYSTEM_TITLE.ERB', '@SYSTEM_TITLE\n', '@SYSTEM_TITLE\nCALL STORY_SYSTEM_TEST\nQUIT\n')
patch(folder / '物品确认.ERB', '$RAIN_ROOM_REVIEW_ITEMS_INPUT\nESCAPE 0\nINPUT', '''$RAIN_ROOM_REVIEW_ITEMS_INPUT
ESCAPE 0
GLOBAL:811 += 1
IF GLOBAL:810 == 3
    RESULT = 0
ELSE
    RESULT = 1
ENDIF''')
patch(folder / '流程.ERB', '$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nINPUT',
      '$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nRESULT = 0')
# A deliberately rejected reply must return to this automated caller without waiting for a key.
flow = folder / '流程.ERB'
flow.write_text(flow.read_text(encoding='utf-8-sig').replace('\n    WAIT\n', '\n    ; test returns after rejected request\n'), encoding='utf-8-sig')
patch(stage / 'ERB/SYSTEM_SCENARIO_LOAD.ERB', 'CALL SCENARIO_CALL_HOOK, 3\nBEGIN TITLE', 'CALL STORY_SYSTEM_AFTER_LOAD\nQUIT')

test_script = f'''@STORY_SYSTEM_TEST
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL INIT_DISPLAY_SETTINGS
CALL RAIN_ROOM_ENSURE_FOOD
AICONNECTION 2
RESULTS:0 '= "http://127.0.0.1:{server.server_port}/v1"
AICONNECTION 3
RESULTS:0 '= "mock-story-system"
AICONNECTION 4
RESULTS:0 '= "fake-loopback-story-key"
AICONNECTION 5
CALL NEW_RAIN_ROOM_AI_HISTORY
FLAG:34 = 0
FLAG:35 = 1
GLOBAL:810 = 1
CALL RAIN_ROOM_REQUEST_ACTION, "整理柜子并取出干净布条"
IF TIME != 1 || STRFIND(SAVESTR:61, "左手指浅划伤") < 0
    THROW failed to establish item and injury
ENDIF
SAVETEXT SAVESTR:61, "story-system-seed.txt", 0, 1
GLOBAL:810 = 2
CALL RAIN_ROOM_REQUEST_ACTION, "检查一下质地", 12, 0, 0, 0
IF TIME != 2
    THROW context linked item did not finish
ENDIF
SAVETEXT SAVESTR:61, "story-system-context.txt", 0, 1
SAVESTR:69 '= SAVESTR:61
GLOBAL:810 = 3
CALL RAIN_ROOM_REQUEST_ACTION, "处理左手指的伤势", 12, 1, 1, 0
IF SAVESTR:61 != SAVESTR:69 || TIME != 2
    THROW cancelling local use changed items injury or time
ENDIF
GLOBAL:810 = 4
CALL RAIN_ROOM_REQUEST_ACTION, "处理左手指的伤势", 12, 1, 1, 0
IF SAVESTR:61 != SAVESTR:69 || TIME != 2
    THROW malformed output partially committed local use
ENDIF
GLOBAL:810 = 5
CALL RAIN_ROOM_REQUEST_ACTION, "处理左手指的伤势", 12, 1, 1, 0
IF SAVESTR:61 != SAVESTR:69 || TIME != 2
    THROW model duplicate material debit changed items injury or time
ENDIF
GLOBAL:810 = 6
CALL RAIN_ROOM_REQUEST_ACTION, "处理左手指的伤势", 12, 1, 1, 0
IF TIME != 3 || STRFIND(SAVESTR:61, "程序包扎") < 0
    THROW explicit local treatment was not adopted
ENDIF
SAVETEXT SAVESTR:61, "story-system-bandage.txt", 0, 1
GLOBAL:810 = 7
CALL RAIN_ROOM_REQUEST_ACTION, "和艾拉约定出行前先列清单"
SAVETEXT SAVESTR:61, "story-system-pending.txt", 0, 1
GLOBAL:810 = 8
CALL RAIN_ROOM_REQUEST_ACTION, "说说集市约定"
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= "1"
RESULTS:2 '= ""
AICONNECTION 77
IF RESULT < 0
    THROW manual memory acceptance failed
ENDIF
SAVESTR:61 '= RESULTS:0
SAVETEXT SAVESTR:61, "story-system-accepted.txt", 0, 1
GLOBAL:810 = 9
CALL RAIN_ROOM_REQUEST_ACTION, "再说说集市约定"
GLOBAL:810 = 10
CALL RAIN_ROOM_REQUEST_ACTION, "安静观察屋外的云"
GLOBAL:810 = 11
CALL RAIN_ROOM_REQUEST_ACTION, "分开行动，艾拉去村落集市，我去溪边"
SAVETEXT SAVESTR:61, "story-system-separated.txt", 0, 1
CALL RAIN_ROOM_SHORT_REST
SAVETEXT SAVESTR:61, "story-system-rested.txt", 0, 1
SAVETEXT SAVESTR:19, "story-system-rest-story.txt", 0, 1
SAVESTR:70 '= SAVESTR:61
CALL CAPTURE_RAIN_ROOM_AI_HISTORY
SAVEDATA 0, "story system roundtrip"
RESULTS:0 '= SAVESTR:61
RESULTS:1 '= "1"
AICONNECTION 74
IF RESULT < 0
    THROW test memory mutation failed
ENDIF
SAVESTR:61 '= RESULTS:0
LOADDATA 0
THROW saved story system was not loaded

@STORY_SYSTEM_AFTER_LOAD
IF SAVESTR:61 != SAVESTR:70
    THROW loaded items injury memories or scene differ from saved snapshot
ENDIF
CALL RAIN_ROOM_AFTER_LOAD
SAVETEXT SAVESTR:61, "story-system-restored.txt", 0, 1
SAVETEXT "PASS story system", "story-system-pass.txt", 0, 1
QUIT
'''
(stage / 'ERB/STORY_SYSTEM_TEST.ERB').write_text(test_script, encoding='utf-8-sig')
proc = subprocess.Popen([str(stage / 'EraAI.exe')])
try:
    marker = stage / 'story-system-pass.txt'
    deadline = time.monotonic() + 65
    while proc.poll() is None and not marker.exists() and time.monotonic() < deadline:
        time.sleep(.15)
    if not marker.exists():
        for log in stage.glob('*.log'):
            print(log.read_text(encoding='utf-8-sig', errors='replace')[-8000:])
        print('mock errors:', server_errors)
        print('requests completed:', len(requests))
    assert not server_errors, server_errors
    assert marker.exists(), 'actual ERB/native story integration did not finish'
    assert len(requests) == 11, len(requests)
    read = lambda name: json.loads((stage / name).read_text(encoding='utf-8-sig'))
    quantity = lambda state: next(item for item in state['items'] if item['id'] == 12)['quantities']['player']
    seed = read('story-system-seed.txt')
    context = read('story-system-context.txt')
    bandage = read('story-system-bandage.txt')
    assert quantity(seed) == quantity(context) == 2
    assert quantity(bandage) == 1 and len(bandage['statuses']) == 1
    assert '程序包扎' in bandage['statuses'][0]['description'] and '未痊愈' in bandage['statuses'][0]['description']
    pending = read('story-system-pending.txt')
    accepted = read('story-system-accepted.txt')
    assert len(pending['memory_proposals']) == 1 and pending['memories'] == []
    assert accepted['memory_proposals'] == [] and len(accepted['memories']) == 1
    assert accepted['memories'][0]['source'] == 'ai'
    separated = read('story-system-separated.txt')
    rested = read('story-system-rested.txt')
    assert separated['character_locations']['ella'] == rested['character_locations']['ella'] == '村落集市'
    assert rested['scene']['present_characters'] == ['player']
    assert rested['character_locations']['player'] == separated['character_locations']['player'] == '屋外不远的溪边'
    assert '独自' in (stage / 'story-system-rest-story.txt').read_text(encoding='utf-8-sig')
    assert read('story-system-restored.txt') == rested
    history = json.loads((stage / '数据/记忆/rain_room.json').read_text(encoding='utf-8-sig'))
    assert len(history) == 9  # 8 adopted API turns + one local rest; three failures/cancellations excluded.
    assert all('inventory_ops' not in turn['Reply'] for turn in history)
    assert all('time' in turn for request in requests[1:] for turn in structured_context(request)['adopted_history'])
    print('PASS actual game: selected item, local use, atomic cancel/failure, memory approval/relevance, separate locations and save/load', flush=True)
finally:
    if proc.poll() is None:
        proc.terminate()
    proc.wait(timeout=10)
    server.shutdown()
    thread.join(timeout=2)
