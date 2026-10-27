using System.Text.Json;
using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.NativeDisplay;

internal static class SceneMapDocumentTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("通过：" + message); }
    internal static void Run()
    {
        var scenario=ScenarioFixture.Ella;
        var world=new InventoryState();
        world.BindCharacters(scenario.Characters); world.BindWorld(scenario); world.SetScene(scenario.InitialScene);
        world.KnownScenes[0].Description="旧档自行修改的描述 </script><script>throw 'bad'</script> /*__WORLD__*/";
        world.KnownScenes[0].Map=null;
        world.SceneConditions[world.Scene!.Location]=new(){["壁炉"]="燃烧中"};
        string before=world.Snapshot();
        var theme=new DisplayTheme(0xDCDCDC,0x0F0F12,0xFFFF00);
        using var payload=JsonDocument.Parse(SceneMapDocument.BuildPayload(world,scenario.World,theme));
        var root=payload.RootElement;
        Check(root.GetProperty("sites").GetArrayLength()==5&&root.GetProperty("currentId").GetInt32()==1,"地图展示快照使用当前存档地点及已知地点");
        var first=root.GetProperty("sites")[0];
        Check(first.GetProperty("description").GetString()==world.KnownScenes[0].Description&&first.GetProperty("map").ValueKind==JsonValueKind.Object,"旧档只补显示布局，保留旧描述");
        Check(first.GetProperty("people").GetArrayLength()==2&&first.GetProperty("conditions")[0].GetString()=="壁炉：燃烧中","地图读取本局人物位置与设施状态");
        Check(root.GetProperty("layout").GetProperty("sites").GetArrayLength()==5,"详细地形布局保存在剧本目录并按需加载");
        string document=SceneMapDocument.Create(world,scenario.World,theme);
        Check(!document.Contains("</script><script>throw")&&document.Contains("\\u003C"),"地图安全编码玩家文本，描述不能执行脚本");
        Check(world.Snapshot()==before,"生成网页及布局不修改库存、剧情、人物或存档");
        var moved=InventoryState.Read(before);
        moved.Scene!.Location="村落集市";moved.Scene.PresentCharacters=[];
        using var movedPayload=JsonDocument.Parse(SceneMapDocument.BuildPayload(moved,scenario.World,theme));
        Check(movedPayload.RootElement.GetProperty("currentId").GetInt32()==5,"当前位置随本局变化，不固定开场地点");
        var empty=new InventoryState();
        using var emptyPayload=JsonDocument.Parse(SceneMapDocument.BuildPayload(empty,null,theme));
        Check(emptyPayload.RootElement.GetProperty("currentId").GetInt32()==0&&emptyPayload.RootElement.GetProperty("sites").GetArrayLength()==0,"无场景及未知位置不虚构地点");
        var custom=new InventoryState();custom.KnownScenes.Add(new(){Id=1000000001,Name="其他剧本的地点",Description="独立资料"});custom.NextSceneId=1000000002;
        using var customPayload=JsonDocument.Parse(SceneMapDocument.BuildPayload(custom,null,theme));
        Check(customPayload.RootElement.GetProperty("layout").ValueKind==JsonValueKind.Null&&customPayload.RootElement.GetProperty("sites")[0].GetProperty("layoutId").GetInt32()==0,"其他剧本及大编号不继承测试剧本地形");
        string directory=Path.Combine(Path.GetTempPath(),"era-map-layout-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var bad=new SceneCatalog{MapFile="bad.json",SourceDirectory=directory};
            File.WriteAllText(Path.Combine(directory,"bad.json"),"{\"format_version\":1,\"sites\":[],\"meters_per_unit\":0}");
            try{SceneMapDocument.BuildPayload(empty,bad,theme);throw new Exception("invalid scale accepted");}catch(ArgumentException){Check(true,"错误地图布局可被安全后备接管");}
            bad.MapFile="../outside.json";
            try{SceneMapDocument.BuildPayload(empty,bad,theme);throw new Exception("outside path accepted");}catch(ArgumentException){Check(true,"地图布局不读取剧本目录外文件");}
        }
        finally { Directory.Delete(directory,true); }
    }
}
