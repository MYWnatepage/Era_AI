using System.Net;
using System.Text;
using System.Text.Json;
using MinorShift.Emuera.NativeAI;

internal static class ReplyConsistencyTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("通过：" + message); }
    internal static async Task Run()
    {
        var state = new InventoryState();
        state.BindCharacters(new Dictionary<string,string> { ["player"]="主角" });
        state.AddInitial(1,"材料","包扎材料","消耗品","",2,"player","");
        var context = new InventoryExchange(state.Snapshot(),"cue-turn");
        string response=JsonSerializer.Serialize(new { story="你收好两条布条。",turn_id=context.TurnId,base_revision=0,inventory_ops=Array.Empty<object>() });
        var snapshot=InventoryReply.Apply(context,response).Snapshot;
        Check(ReplyConsistency.MissingItemOperations("我整理箱子时找到两条干净布条，收进物品里。",context.Snapshot,snapshot).Contains("没有物品变更"),
            "明确获得物品但没有物品操作时提示核对，不让空操作悄悄通过");
        foreach (string text in new[]{"你好","我没有找到两条布条。","如果找到两条布条就好了。","我打算买下两份材料。","昨天找到两条布条。","她问哪里能找到两条布条。","她说：\"找到两条布条\"。"})
            Check(ReplyConsistency.MissingItemOperations(text,context.Snapshot,snapshot)=="","交谈、否定、计划、回顾或引用不会因核对提示自动结算");
        var changed=InventoryState.Read(snapshot); changed.Item(1).Quantities["player"]--;
        Check(ReplyConsistency.MissingItemOperations("我用掉一份材料。",context.Snapshot,changed.Snapshot())=="","已有物品变化不重复触发空操作提示");
        Check(ReplyConsistency.MissingStoryChanges("你用掉一条布条，随后继续交谈。", context.Snapshot, snapshot).Contains("没有物品变更"), "玩家没写数量时也可核对剧情中明确的消耗遗漏，但不会自动扣物");
        Check(ReplyConsistency.MissingStoryChanges("你独自走到溪边。", context.Snapshot, snapshot).Contains("当前场景未变"), "剧情中的明确移动遗漏进入核对提示");
        string directory=Path.Combine(Path.GetTempPath(),"EraAI-consistency-"+Guid.NewGuid().ToString("N"));
        int calls=0;bool checkSystem=false;string seenFormat="";
        try
        {
            var memory=new ConversationStore(directory);
            var client=new AiClient(new HttpClient(new Handler(async request=>
            {
                calls++;
                using var payload=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                string system=payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                seenFormat=payload.RootElement.TryGetProperty("response_format",out var format)?format.GetProperty("type").GetString()!:"";
                checkSystem=system.Contains("inventory_ops")&&system.Contains("state_ops")&&system.Contains("保持未知")&&
                    !system.Contains("cue-turn")&&!system.Contains("覆盖前面的");
                return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{content=response},finish_reason="stop"}}}),Encoding.UTF8,"application/json")};
            })),memory);
            var settings=new AiSettings{Mode="api",BaseUrl="https://example.invalid/v1",Model="test",ProtectedKey=SecretProtection.Protect("fake-test-key"),TimeoutSeconds=10};
            var result=await client.SendAsync(settings,new AiRequest("test","我找到两条布条。","other_scenario","第1天",1024,0,"",new GenerationSettings(),Inventory:context),default);
            Check(result.Success&&result.RequiresApproval&&result.Warning.Contains("物品核对")&&checkSystem,"请求加入完整结算约束；漏操作结果进入人工核对而非静默采用");
            Check(state.Items.Count==1&&state.Quantity(1)==2&&memory.Read("other_scenario").Count==0&&calls==1,
                "核对提示不会凭关键词造物、改库存、提前写记忆或自动增加API请求");
            Check(seenFormat=="","未知兼容服务不强塞未经确认的JSON参数");
            var deepseek=settings with{BaseUrl="https://api.deepseek.com/v1",Model="deepseek-flash"};
            var structuredRequest=new AiRequest("test","你好","other_scenario","第1天",1024,0,"",new GenerationSettings(),Inventory:context);
            await client.SendAsync(deepseek,structuredRequest,default);
            Check(seenFormat=="json_object","DeepSeek正式结构化回合启用官方JSON输出模式");
            await client.SendAsync(deepseek,structuredRequest with{Scenario="connection_test",Inventory=null,Parameters=null},default);
            Check(seenFormat==""&&calls==3,"连接测试不启用剧情JSON模式，也不会自动重发请求");
        }
        finally { if(Directory.Exists(directory)) Directory.Delete(directory,true); }
        try {InventoryReply.Apply(context,"{bad-json");throw new Exception("未拒绝损坏格式");}
        catch(InvalidOperationException e){Check(e.Message.Contains("JSON")&&e.Message.Contains("未变"),"格式失败给出具体且不含原始回复的原因");}
        string insufficient=JsonSerializer.Serialize(new {story="消耗",turn_id=context.TurnId,base_revision=0,inventory_ops=new[]{new {op="change_quantity",item_id=1,owner="player",delta=-3,reason="test"}}});
        try {InventoryReply.Apply(context,insufficient);throw new Exception("未拒绝库存不足");}
        catch(InvalidOperationException e){Check(e.Message.Contains("数量不足")&&e.Message.Contains("未变"),"库存不足与格式失败可区分，保持整轮不结算");}
    }
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> response):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)=>response(request);
    }
}
