using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MinorShift.Emuera.NativeAI;
using MinorShift.Emuera.NativeDisplay;

internal static class NativeSceneWorldTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("通过：" + message); }

    internal static void Run()
    {
        var scenario = ScenarioFixture.Ella;
        var state = new InventoryState();
        state.BindCharacters(scenario.Characters); state.BindWorld(scenario); state.SetScene(scenario.InitialScene);
        state.SceneConditions[state.Scene!.Location] = new() { ["壁炉"] = "燃烧中" };
        string before = state.Snapshot();
        string payload = SceneMapDocument.BuildPayload(state, scenario.World, new DisplayTheme(0xDCDCDC, 0x0F0F12, 0xFFFF00));
        var world = NativeSceneWorld.Build(payload);
        Check(world.Sites.Count == 5 && world.CurrentId == 1 && world.Origin == new Vector2(-25, 18) && world.MetersPerUnit == 10,
            "原生几何读取相同地点、当前位置、坐标原点和示意比例");
        Check(world.Sites[0].People.Length == 2 && world.Sites[0].Conditions.Contains("壁炉：燃烧中") && state.Snapshot() == before,
            "原生地点保留本局人物及设施资料，不修改存档");

        // Reference values produced by the approved world.js in Node's VM, before adding barycentric attributes.
        Check(world.Terrain.Length == 7192 * 30 && world.Solids.Length == 628 * 30 && world.River.Length == 440 * 30 && world.Routes.Length == 222 * 30 &&
            world.TerrainEdgeFloatCount == 87264 && world.Edges.Length == 94592,
            "原生地形、建筑树林、河流、道路和边线数量与既有三维地图一致");
        var heights = new (float X, float Z, double Height)[]
        {
            (0, 0, 4.5960435163109965), (-25, 18, 6.483275750642841), (12.189175455276285f, 7, 2.309444568357402),
            (41, -19, 10.698106157180142), (-20, -25, 5.805105831110711)
        };
        Check(heights.All(sample => Math.Abs(world.HeightAt(sample.X, sample.Z) - sample.Height) < .0001),
            "原生高度场的山坡、河谷与各地点采样高度和既有地图一致");
        var vertices = new (int Vertex, float[] Values)[]
        {
            (0, [-110, 2.6656181812286377f, -110, .056914832442998886f, .9975250363349915f, .04128602519631386f, 0]),
            (123, [-35.48387145996094f, 2.304614543914795f, -110, .005008570849895477f, .9982129335403442f, -.05954672396183014f, 0]),
            (1000, [38.38755416870117f, 7.274665355682373f, -99.30472564697266f, .12747441232204437f, .9886258840560913f, -.07980690151453018f, 0]),
            (9000, [-68.04034423828125f, 4.994755744934082f, -18.612491607666016f, -.02142554521560669f, .9962878823280334f, -.08337493985891342f, 0]),
            (21575, [110, 6.646458625793457f, 110, .0818183422088623f, .9966471195220947f, -.0005117406253702939f, 0])
        };
        Check(vertices.All(sample => sample.Values.Select((value, index) => Math.Abs(world.Terrain[sample.Vertex * 10 + index] - value) < .0001).All(equal => equal)),
            "原生三角网格的分割、顶点位置及法线与既有地图采样一致");
        foreach (var mesh in new[] { world.Terrain, world.Solids, world.River, world.Routes }) ValidateMesh(world, mesh);
        Check(world.Edges.Length % 8 == 0 && world.Edges.All(float.IsFinite), "原生边线按完整线段保存且所有值有限");

        var subsetPayload = JsonNode.Parse(payload)!.AsObject();
        var subsetSites = subsetPayload["sites"]!.AsArray();
        subsetSites.Remove(subsetSites.Single(site => site!["id"]!.GetValue<int>() == 4));
        var subset = NativeSceneWorld.Build(subsetPayload.ToJsonString());
        Check(subset.Sites.Count == 4 && subset.Solids.Length == (628 - 26 * 15) * 30 && subset.Sites.All(site => site.Id != 4),
            "未被存档发现的林地不会绘出其二十六棵所属树木");
        Check(subset.Terrain.SequenceEqual(world.Terrain), "改变已知地点集合不会重新排列或改变既有地形");

        const string generic = """
            {"currentId":1000000001,"sites":[
                {"id":1000000001,"name":"轨道站","description":"其他剧本独立描述","kind":"place"},
                {"id":7,"name":"补给站","map":{"x":0.25,"y":0.75,"symbol":"cabin"}}
            ]}
            """;
        var independent = NativeSceneWorld.Build(generic);
        Check(independent.CurrentId == 1000000001 && independent.Sites[0].Id == 1000000001 && independent.Sites[0].RenderId == 1 &&
            independent.RenderIdToSiteId[1] == 1000000001 && independent.Sites[0].Description == "其他剧本独立描述" && independent.Origin == Vector2.Zero,
            "原生通用地图支持非连续大编号，真实地点编号不写入浮点绘图属性");
        Check(independent.Sites[1].X == -37.4f && independent.Sites[1].Z == 37.4f && independent.Sites.All(s => !s.HasLayout),
            "独立剧本可使用归一化坐标及通用符号，不继承测试剧本地形");
        var reordered = JsonNode.Parse(generic)!.AsObject();
        var sourceSites = reordered["sites"]!.AsArray();
        var firstSite = sourceSites[0]!.DeepClone(); sourceSites.RemoveAt(0); sourceSites.Add(firstSite);
        sourceSites.Add(JsonNode.Parse("{\"id\":99,\"name\":\"新地点\"}"));
        var expanded = NativeSceneWorld.Build(reordered.ToJsonString());
        Check(independent.Sites.All(site => expanded.Sites.Any(other => other.Id == site.Id && other.X == site.X && other.Z == site.Z)),
            "新增地点和改变列表顺序不会挪动已有自动坐标");
        ValidateMesh(independent, independent.Solids);
        var empty = NativeSceneWorld.Build("{\"sites\":[],\"currentId\":0}");
        Check(empty.Sites.Count == 0 && empty.CurrentId == 0 && empty.Solids.Length == 0 && empty.Terrain.All(float.IsFinite),
            "空地图不虚构当前地点且仍可安全绘制有限地形");
        string manyPayload = JsonSerializer.Serialize(new { sites = Enumerable.Range(1, 200).Select(id => new { id, name = "地点" + id }) });
        var many = NativeSceneWorld.Build(manyPayload);
        Check(many.Sites.Count == 192 && many.Sites.Max(site => site.RenderId) == 192 && many.Solids.All(float.IsFinite),
            "批量新增地点限制紧凑绘图编号并保持有效几何");
    }

    private static void ValidateMesh(NativeSceneWorld world, float[] mesh)
    {
        Check(mesh.Length % 30 == 0 && mesh.All(float.IsFinite), "原生三角网格包含完整顶点、法线与重心属性且没有非有限值");
        for (int i = 0; i < mesh.Length; i += 10)
        {
            float owner = mesh[i + 6];
            if (owner != 0 && (owner != MathF.Truncate(owner) || !world.RenderIdToSiteId.ContainsKey((int)owner))) throw new Exception("Invalid compact owner");
            int vertex = i / 10 % 3;
            if (mesh[i + 7] != (vertex == 0 ? 1 : 0) || mesh[i + 8] != (vertex == 1 ? 1 : 0) || mesh[i + 9] != (vertex == 2 ? 1 : 0)) throw new Exception("Invalid barycentric attributes");
            float normalLength = new Vector3(mesh[i + 3], mesh[i + 4], mesh[i + 5]).Length();
            if (normalLength != 0 && Math.Abs(normalLength - 1) > .0001) throw new Exception("Invalid normal");
        }
    }
}
