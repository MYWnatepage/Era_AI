import sys
sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
from test_support import structured_context, TEST_ROOT, GAME_DIR
from http.server import HTTPServer, BaseHTTPRequestHandler
import json, re, shutil, subprocess, threading, time

for case in ['lifecycle', 'cancel', 'invalid']:
    requests = []
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_): pass
        def do_POST(self):
            request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            requests.append(request)
            system = request['messages'][0]['content']
            assert 'state_ops' in system
            context = structured_context(request)
            turn = context['turn_id']
            revision = int(context['base_revision'])
            index = len(requests)
            if index == 1:
                ops = [dict(op='create_item', name='绷带', description='干净的布条。', type='消耗品', attributes='包扎', quantity=1, owner='player', reason='本轮整理物资找到')]
                statuses = [dict(op='create_status', character='stranger' if case == 'invalid' else 'player', name='左臂划伤',
                                 category='伤势', severity='轻微', description='左前臂有一道浅伤口，尚未处理。', effects='拉弓时疼痛', reason='本轮穿过灌木时划伤')]
                story = '你在灌木中划伤了左臂，回家整理出一条干净绷带。'
            elif index == 2:
                ops = [dict(op='change_quantity', item_id=12, owner='player', delta=-1, reason='用绷带包扎')]
                statuses = [dict(op='update_status', status_id=1, character='player', name='左臂划伤', category='伤势', severity='轻微',
                                 description='伤口已经包扎，尚未痊愈。', effects='用力时仍然疼痛', reason='本轮使用绷带处理伤口')]
                story = '你用绷带包好左臂，伤口仍需休养。'
            else:
                ops = []
                statuses = [dict(op='remove_status', status_id=1, character='player', reason='测试中确认状态已经失效')]
                story = '你检查左臂，确认这次伤势已经结束。'
            content = dict(story=story, turn_id=turn, base_revision=revision, inventory_ops=ops, state_ops=statuses, suggestions=[], effort='none')
            body = json.dumps(dict(choices=[dict(message=dict(content=json.dumps(content)), finish_reason='stop')])).encode()
            self.send_response(200); self.end_headers(); self.wfile.write(body)
    stage = TEST_ROOT / ('character-status-' + case)
    shutil.copytree(GAME_DIR, stage, ignore=shutil.ignore_patterns('配置','数据','*.sav','*.log','profile','setting.json','emuera.config'))
    (stage/'配置').mkdir()
    (stage/'配置/music-settings.json').write_text(json.dumps(dict(Enabled=False,Volume=50)),encoding='utf-8')
    server = HTTPServer(('127.0.0.1',0),Handler)
    thread = threading.Thread(target=server.serve_forever,daemon=True); thread.start()
    folder = stage/'ERB/script/艾拉的奇幻冒险'
    p=stage/'ERB/SYSTEM_TITLE.ERB'
    p.write_text(p.read_text(encoding='utf-8-sig').replace('@SYSTEM_TITLE\n','@SYSTEM_TITLE\nCALL STATUS_TEST\nQUIT\n',1),encoding='utf-8-sig')
    p=folder/'物品确认.ERB'
    p.write_text(p.read_text(encoding='utf-8-sig').replace('$RAIN_ROOM_REVIEW_ITEMS_INPUT\nESCAPE 0\nINPUT',
        '$RAIN_ROOM_REVIEW_ITEMS_INPUT\nESCAPE 0\nGLOBAL:801 += 1\nRESULT = '+('0' if case=='cancel' else '1')),encoding='utf-8-sig')
    p=folder/'流程.ERB'
    p.write_text(p.read_text(encoding='utf-8-sig').replace('$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nINPUT',
        '$SCENARIO_TEST_RAIN_ROOM_SHOW_RESPONSE_INPUT\nESCAPE 0\nRESULT = 0').replace('\n    WAIT\n','\n    ; test: return after failure\n'),encoding='utf-8-sig')
    p=folder/'人物状态.ERB'
    s=p.read_text(encoding='utf-8-sig').replace('ESCAPE 0\nINPUT', '''ESCAPE 0
IF LOCALS:12 != "player" || LOCALS:13 != "ella" || STRFIND(LOCALS:0, "主角") < 0 || STRFIND(LOCALS:0, "森林边的小屋") < 0
    THROW character overview missing registered people or location
ENDIF
IF GLOBAL:805 == 1 && (STRFIND(LOCALS:0, "11岁 / 145 cm / 36 kg / 健康") < 0 || STRFIND(LOCALS:1, "7岁 / 120 cm / 22 kg / 健康") < 0)
    THROW initial profiles missing or unhealthy
ENDIF
GLOBAL:802 += 1
IF GLOBAL:802 == 1
    RESULT = 1
ELSE
    RESULT = 0
ENDIF''',1)
    s=s.replace('ESCAPE 0\nINPUT','''ESCAPE 0
IF LOCALS:0 != "主角" || LOCALS:1 != "森林边的小屋"
    THROW selected character mapping or saved location is incorrect
ENDIF
IF LOCALS:24 != "11 岁" || LOCALS:25 != "145 cm" || LOCALS:26 != "36 kg"
    THROW character profile missing from detail
ENDIF
GLOBAL:804 += 1
IF GLOBAL:805 == 1
    IF LOCALS:2 != "0" || LOCALS:4 != "" || LOCALS:14 != ""
        THROW empty condition detail invented state information
    ENDIF
    RESULT = 0
ELSE
    IF LOCALS:2 != "1" || STRFIND(LOCALS:4, "左臂划伤") < 0 || LOCALS:14 != "1"
        THROW character condition row or detail mapping is incorrect
    ENDIF
    IF GLOBAL:804 == 1
        RESULT = 1
    ELSE
        RESULT = 0
    ENDIF
ENDIF''',1)
    s=s.replace('ESCAPE 0\nINPUT','''ESCAPE 0
IF LOCALS:0 != "左臂划伤" || LOCALS:1 != "主角" || STRFIND(LOCALS:4, "尚未处理") < 0
    THROW status detail page missing injury
ENDIF
GLOBAL:803 += 1
RESULT = 0''',1)
    p.write_text(s,encoding='utf-8-sig')
    p=stage/'ERB/SYSTEM_SCENARIO_LOAD.ERB'
    p.write_text(p.read_text(encoding='utf-8-sig').replace('CALL SCENARIO_CALL_HOOK, 3\nBEGIN TITLE','CALL STATUS_AFTER_LOAD\nQUIT'),encoding='utf-8-sig')
    common=f'''@STATUS_TEST
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL INIT_DISPLAY_SETTINGS
CALL RAIN_ROOM_ENSURE_FOOD
SAVESTR:69 '= SAVESTR:61
AICONNECTION 2
RESULTS:0 '= "http://127.0.0.1:{server.server_port}/v1"
AICONNECTION 3
RESULTS:0 '= "mock-status"
AICONNECTION 4
RESULTS:0 '= "fake-status-test-key"
AICONNECTION 5
CALL NEW_RAIN_ROOM_AI_HISTORY
CALL RAIN_ROOM_REQUEST_ACTION, "穿过灌木后回家整理物资"
'''
    if case == 'lifecycle':
        common=common.replace('CALL RAIN_ROOM_REQUEST_ACTION, "穿过灌木后回家整理物资"', '''GLOBAL:805 = 1
SAVESTR:71 '= SAVESTR:61
CALL RAIN_ROOM_STATUS_PAGE, 0
IF GLOBAL:802 != 2 || GLOBAL:804 != 1 || GLOBAL:803 != 0 || SAVESTR:61 != SAVESTR:71 || TIME != 0
    THROW empty character overview navigation changed progress or missed people
ENDIF
GLOBAL:805 = 0
GLOBAL:802 = 0
GLOBAL:804 = 0
CALL RAIN_ROOM_REQUEST_ACTION, "穿过灌木后回家整理物资"''')
    if case != 'lifecycle':
        common+='''IF SAVESTR:61 != SAVESTR:69 || TIME != 0 || GLOBAL:510 != 0
    THROW cancelled or invalid status changed inventory time or memory
ENDIF
SAVETEXT "PASS status", "status-pass.txt", 0, 1
QUIT
'''
    else:
        common+='''IF STRFIND(SAVESTR:61, "左臂划伤") < 0 || TIME != 1 || GLOBAL:801 != 1
    THROW initial status proposal not adopted
ENDIF
CALL RAIN_ROOM_STATUS_PAGE, 0
IF GLOBAL:802 != 2 || GLOBAL:804 != 2 || GLOBAL:803 != 1 || TIME != 1
    THROW status view navigation changed time or missed detail
ENDIF
SAVESTR:70 '= SAVESTR:61
SAVETEXT SAVESTR:61, "status-injured.txt", 0, 1
CALL CAPTURE_RAIN_ROOM_AI_HISTORY
SAVEDATA 0, "injury snapshot"
CALL RAIN_ROOM_REQUEST_ACTION, "用绷带包扎左臂"
IF STRFIND(SAVESTR:61, "伤口已经包扎") < 0 || TIME != 2 || GLOBAL:801 != 2
    THROW treatment not committed
ENDIF
SAVETEXT SAVESTR:61, "status-treated.txt", 0, 1
CALL RAIN_ROOM_REQUEST_ACTION, "确认旧状态已失效"
IF STRFIND(SAVESTR:61, "左臂划伤") >= 0 || TIME != 3 || GLOBAL:801 != 3
    THROW removed status remains active
ENDIF
SAVETEXT SAVESTR:61, "status-removed.txt", 0, 1
LOADDATA 0
THROW load event not entered

@STATUS_AFTER_LOAD
IF SAVESTR:61 != SAVESTR:70 || TIME != 1
    THROW saved status snapshot was not restored
ENDIF
CALL RAIN_ROOM_AFTER_LOAD
IF STRFIND(SAVESTR:61, "尚未处理") < 0
    THROW load hook cleared saved injury
ENDIF
SAVETEXT SAVESTR:61, "status-restored.txt", 0, 1
; Starting over removes runtime statuses without changing the initial character card.
CALL INIT_NEWGAME
CALL INIT_RAIN_ROOM_GAME
CALL RAIN_ROOM_ENSURE_FOOD
IF STRFIND(SAVESTR:61, "左臂划伤") >= 0 || FLAG:20 != 10
    THROW fresh game retained prior statuses
ENDIF
SAVETEXT "PASS status", "status-pass.txt", 0, 1
QUIT
'''
    (stage/'ERB/STATUS_TEST.ERB').write_text(common,encoding='utf-8-sig')
    proc=subprocess.Popen([str(stage/'EraAI.exe')])
    try:
        marker=stage/'status-pass.txt';deadline=time.monotonic()+35
        while proc.poll() is None and not marker.exists() and time.monotonic()<deadline: time.sleep(.15)
        if not marker.exists():
            for log in stage.glob('*.log'): print(log.read_text(encoding='utf-8-sig',errors='replace')[-6000:])
        assert marker.exists(),case+': character status engine test did not finish'
        assert len(requests)==(3 if case=='lifecycle' else 1)
        if case=='lifecycle':
            read=lambda name:json.loads((stage/name).read_text(encoding='utf-8-sig'))
            injured=read('status-injured.txt');treated=read('status-treated.txt');removed=read('status-removed.txt')
            assert injured['statuses'][0]['created_at']=='第1天 时间0/8'
            assert treated['statuses'][0]['description']=='伤口已经包扎，尚未痊愈。'
            assert treated['statuses'][0]['updated_at']=='第1天 时间1/8'
            assert next(i for i in treated['items'] if i['id']==12)['quantities']['player']==0
            assert removed['statuses']==[] and removed['next_status_id']==2
            assert read('status-restored.txt')==injured
            assert '尚未处理' in requests[1]['messages'][-1]['content']
            assert '尚未痊愈' in requests[2]['messages'][-1]['content']
        else:
            memory=json.loads((stage/'数据/记忆/rain_room.json').read_text(encoding='utf-8-sig'))
            assert memory==[]
        print('PASS character status engine: '+case,flush=True)
    finally:
        if proc.poll() is None: proc.terminate()
        proc.wait(timeout=10);server.shutdown();thread.join(timeout=2)
