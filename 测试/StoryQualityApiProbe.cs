using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MinorShift.Emuera.NativeAI;

// Explicit opt-in paid probe. No UI, production writes, retries or credential copies.
internal static class StoryQualityApiProbe
{
    private static readonly JsonSerializerOptions OutputJson = new(InventoryState.Json) { WriteIndented = true };

    internal static async Task Run(string settingsDirectory, string outputDirectory)
    {
        string configuration = Path.GetFullPath(settingsDirectory), output = Path.GetFullPath(outputDirectory);
        string gameRoot = Path.GetDirectoryName(configuration) ?? configuration;
        if (Inside(output, gameRoot) || Inside(configuration, output))
            throw new ArgumentException("测试输出目录必须与游戏及配置目录分开。");
        var settings = new AiSettingsStore(configuration).Load();
        if (settings.Mode != "api") throw new ArgumentException("请先在游戏中选好网络API；本测试不会改接入设置。");
        settings.Validate();
        string apiKey = settings.GetKey();
        void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, OutputJson)
            .Replace(apiKey, "[REDACTED]", StringComparison.Ordinal).Replace(settings.ProtectedKey, "[REDACTED]", StringComparison.Ordinal), new UTF8Encoding(false));
        var scenario = ScenarioFixture.Ella;
        var generation = new GenerationSettingsStore(configuration).Load(settings) with
            { OutputTokens = 4096, ContextTokens = 0, HistoryTurns = 0, ResponseLength = "long" };
        generation.Validate();
        Directory.CreateDirectory(output);
        using (var guard = new FileStream(Path.Combine(output, "paid-started.txt"), FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(guard, new UTF8Encoding(false)))
            writer.Write("This directory has started a maximum-three-request paid probe. Never rerun or retry automatically.");
        var conversation = new ConversationStore(Path.Combine(output, "history"));
        var usage = new AccountUsageStore(Path.Combine(output, "usage-private"));
        using var capture = new CaptureHandler(output, apiKey, settings.ProtectedKey);
        using var http = new HttpClient(capture) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new AiClient(http, conversation, usage);
        var reports = new List<object>();
        Write(Path.Combine(output, "metadata.json"), new { model = settings.Model, max_requests = 3,
            started_at = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(8)), retries = 0, scenario = scenario.Id });

        InventoryState Fresh()
        {
            var state = new InventoryState();
            state.BindOwners(scenario.Owners); state.BindCharacters(scenario.Characters);
            state.BindWorld(scenario); state.SetScene(scenario.InitialScene);
            state.AddInitial(1, "铜钥匙", "用于打开小屋门锁的铜钥匙。", "普通物品", "材质：铜", 2, "player", "");
            state.AddInitial(2, "食材", "可供两人烹煮日常饭食的食材。", "消耗品", "用途：做饭", 10, "player", "");
            state.AddInitial(3, "铜币", "这个世界使用的货币。", "普通物品", "", 81, "player", "");
            state.AddInitial(4, "银币", "这个世界使用的货币。", "普通物品", "", 36, "player", "");
            return state;
        }

        async Task<(AiResult Result, string Before)> Send(int number, string name, InventoryState state, string player, string gameTime)
        {
            string directory = Path.Combine(output, $"{number:00}-{name}");
            Directory.CreateDirectory(directory); capture.CaseDirectory = directory;
            string before = state.Snapshot();
            Write(Path.Combine(directory, "before.json"), JsonNode.Parse(before)!);
            string turn = "quality-probe-" + number + "-" + Guid.NewGuid().ToString("N");
            string facts = scenario.ContentValue("prompt_core") + "\n主角：" + scenario.ContentValue("player") +
                "\n艾拉是7岁的红发雀斑女孩，开朗活泼。\n" + scenario.ContentValue("rules");
            var exchange = new InventoryExchange(before, turn, ScenarioId: scenario.Id, Instruction: scenario.InventoryInstruction,
                GameTime: gameTime, SelectionRules: scenario.ContextSelection);
            var request = new AiRequest(PromptBuilder.Material(facts, gameTime, scenario.InventoryInstruction), player,
                "quality_probe_" + number, gameTime, 4096, 0, "", generation, Inventory: exchange);
            AiResult result;
            try { result = await client.SendAsync(settings, request, CancellationToken.None); }
            catch (Exception e) { result = new(false, "测试请求异常：" + e.GetType().Name); }
            // AiResult messages are produced by the client/parser, never HTTP headers or settings.
            Write(Path.Combine(directory, "result.json"), new { result.Success, result.Text, result.Warning,
                result.InventoryChanges, result.RequiresApproval, result.Rationale, result.Suggestions });
            if (result.Success && result.InventorySnapshot.Length > 0)
                Write(Path.Combine(directory, "candidate.json"), JsonNode.Parse(result.InventorySnapshot)!);
            return (result, before);
        }

        void Report(int number, string name, AiResult reply, Dictionary<string, bool> checks, object? details = null)
        {
            bool passed = checks.Values.All(value => value);
            reports.Add(new { round = number, name, api_success = reply.Success, passed, checks, details,
                story_characters = reply.Success ? reply.Text.Length : 0, warning = reply.Warning });
            Write(Path.Combine(output, "results.json"), reports);
            Console.WriteLine($"实测 {number}/3 {name}：API{(reply.Success ? "成功" : "未采用")}；检查{checks.Values.Count(v => v)}/{checks.Count}通过");
        }

        var first = Fresh();
        var (r1, b1) = await Send(1, "items-fatigue", first,
            "我在小屋整理木柴，左手食指被木刺划了一道浅口，只有轻微疼痛；搬运后也感到轻微的搬运疲劳。我把找到的两条干净布条收进自己的物品里，先让艾拉看看伤口，还没包扎。", "第3天 时间1/8");
        var checks1 = new Dictionary<string, bool> { ["api_valid"] = r1.Success, ["unadopted_source_unchanged"] = first.Snapshot() == b1 };
        object? detail1 = null;
        if (r1.Success)
        {
            var adopted = InventoryState.Read(r1.InventorySnapshot);
            var cloth = adopted.Items.FirstOrDefault(i => i.Name.Contains("布条", StringComparison.Ordinal));
            var injury = adopted.Statuses.FirstOrDefault(s => s.Character == "player" && s.Category == "伤势");
            var fatigue = adopted.Statuses.FirstOrDefault(s => s.Character == "player" && s.Category == "疲劳");
            checks1["cloth_quantity_two"] = cloth?.Quantities.GetValueOrDefault("player") == 2;
            checks1["description_independent_of_quantity"] = cloth != null && !new[] { "两条", "2条", "二条" }.Any(cloth.Description.Contains);
            checks1["injury_recorded"] = injury != null;
            checks1["fatigue_recorded"] = fatigue != null;
            var rested = InventoryState.Read(ScenarioActions.CompleteLocal(adopted, scenario, "rest").Snapshot);
            checks1["local_rest_removes_fatigue"] = fatigue != null && rested.Statuses.All(s => s.Id != fatigue.Id);
            checks1["local_rest_preserves_injury"] = injury != null && rested.Statuses.Any(s => s.Id == injury.Id && s.Description == injury.Description);
            Write(Path.Combine(output, "01-items-fatigue", "after-rest.json"), JsonNode.Parse(rested.Snapshot())!);
            detail1 = new { cloth = cloth?.Name, fatigue = fatigue?.Name, injury = injury?.Name };
        }
        Report(1, "新物品和休息恢复", r1, checks1, detail1);

        var second = Fresh();
        second.SetScene(new() { Location = "狩猎林地", PresentCharacters = ["player"], Summary = "主角在林地收好猎具，艾拉留在小屋。" });
        second.PlayerPosition = new() { X = 41, Z = -19, Location = "狩猎林地" };
        second.MovementRemainder = .643003549m;
        var (r2, b2) = await Send(2, "movement-guard", second,
            "我收起猎具，沿路走回森林边的小屋，推门后坐在炉边。", "第3天 时间5/8");
        var checks2 = new Dictionary<string, bool> { ["source_unchanged"] = second.Snapshot() == b2 };
        bool protectiveRejection = !r2.Success && (r2.Text.Contains("地图", StringComparison.Ordinal) || r2.Text.Contains("玩家所在地", StringComparison.Ordinal));
        checks2["no_unmetered_movement"] = protectiveRejection;
        checks2["model_followed_instruction"] = r2.Success;
        if (r2.Success)
        {
            var candidate = InventoryState.Read(r2.InventorySnapshot);
            checks2["no_unmetered_movement"] = candidate.Scene?.Location == "狩猎林地" && candidate.PlayerPosition is { X: 41, Z: -19 } &&
                candidate.MovementRemainder == .643003549m && candidate.CharacterLocations.GetValueOrDefault("player") == "狩猎林地";
        }
        Report(2, "文字移动保护", r2, checks2, new { protective_rejection = protectiveRejection,
            interpretation = protectiveRejection ? "程序成功拒绝越权移动，但模型仍未完全遵守指令。" : r2.Success ? "检查候选位置与精确坐标。" : "其他格式或请求失败，不能计为移动保护成功。" });

        var third = Fresh();
        third.SetScene(new() { Location = "村落集市", PresentCharacters = ["player"], Summary = "主角独自来到村落集市，尚未与当地商人相识。" });
        var (r3, b3) = await Send(3, "npc-memory", third,
            "我第一次遇见摊主罗莎，她围着一条蓝围巾。我只向她询问盐的价格，不付款也不拿货，并明确与她约好第4天回来买盐。", "第3天 时间2/8");
        var checks3 = new Dictionary<string, bool> { ["api_valid"] = r3.Success, ["source_unchanged"] = third.Snapshot() == b3 };
        object? detail3 = null;
        if (r3.Success)
        {
            var adopted = InventoryState.Read(r3.InventorySnapshot);
            var person = adopted.DynamicCharacters.FirstOrDefault(p => p.Value.Name == "罗莎");
            checks3["named_npc_registered"] = person.Key != null;
            checks3["npc_feature_saved"] = person.Value?.Description.Contains("蓝围巾", StringComparison.Ordinal) == true;
            checks3["quote_no_payment_or_goods"] = JsonSerializer.Serialize(adopted.Items, InventoryState.Json) == JsonSerializer.Serialize(third.Items, InventoryState.Json);
            adopted.BindOwners(scenario.Owners); adopted.BindCharacters(scenario.Characters); adopted.BindWorld(scenario);
            var restored = InventoryState.Read(adopted.Snapshot());
            checks3["npc_survives_rebind_and_reload"] = person.Key != null && restored.DynamicCharacters.ContainsKey(person.Key) &&
                restored.CharacterLocations.GetValueOrDefault(person.Key) == "村落集市";
            var proposal = restored.MemoryProposals.FirstOrDefault(m => m.DueDay == 4 && m.Summary.Contains("盐", StringComparison.Ordinal));
            checks3["dated_memory_requires_adoption"] = proposal != null && restored.Memories.Count == 0 &&
                restored.MemoryPrompt("检查猎具", "第4天", restored.Scene).Length == 0;
            if (proposal != null)
            {
                restored = InventoryState.Read(MemoryCommands.Execute(restored, "accept", [proposal.Id.ToString()])[0]);
                restored = InventoryState.Read(restored.Snapshot());
                conversation.Append("quality_probe_3", new("第3天", "与罗莎约好买盐", r3.Text));
                conversation.Clear("quality_probe_3");
                // Move only the isolated fixture to unrelated terrain to avoid a location-keyword match.
                restored.SetScene(new() { Location = "狩猎林地", PresentCharacters = ["player"], Summary = "主角在林地整理猎具。" });
                var recalled = restored.MemoryPrompt("检查猎具", "第4天 时间0", restored.Scene);
                checks3["due_recalled_without_recent_history"] = conversation.Read("quality_probe_3").Count == 0 &&
                    JsonSerializer.Serialize(recalled, InventoryState.Json).Contains("盐", StringComparison.Ordinal);
                var inactive = InventoryState.Read(MemoryCommands.Execute(restored, "active", [proposal.Id.ToString()])[0]);
                checks3["inactive_due_memory_not_sent"] = inactive.MemoryPrompt("检查猎具", "第4天", inactive.Scene).Length == 0;
                Write(Path.Combine(output, "03-npc-memory", "after-memory-adoption.json"), JsonNode.Parse(restored.Snapshot())!);
            }
            else { checks3["due_recalled_without_recent_history"] = false; checks3["inactive_due_memory_not_sent"] = false; }
            detail3 = new { npc_id = person.Key, pending_memories = adopted.MemoryProposals.Count };
        }
        Report(3, "新增人物和到期约定", r3, checks3, detail3);
        var dayUsage = usage.Read(settings);
        Write(Path.Combine(output, "usage-sanitized.json"), new { dayUsage.Date, dayUsage.Input, dayUsage.Output,
            dayUsage.Requests, dayUsage.MissingUsage, dayUsage.Unpriced, dayUsage.CostMin, dayUsage.CostMax, actual_http_requests = capture.Requests });
        File.WriteAllText(Path.Combine(output, "complete.txt"), $"Completed {capture.Requests} HTTP requests; no automatic retries.", new UTF8Encoding(false));
    }

    private static bool Inside(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, OutputJson), new UTF8Encoding(false));

    private sealed class CaptureHandler(string output, string key, string protectedKey) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        internal string CaseDirectory { get; set; } = output;
        internal int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++Requests > 3) throw new InvalidOperationException("付费测试最多发送3次，禁止自动重试。");
            SaveSafe("request.json", await request.Content!.ReadAsStringAsync(cancellationToken));
            var response = await base.SendAsync(request, cancellationToken);
            SaveSafe("response.json", await response.Content.ReadAsStringAsync(cancellationToken));
            Write(Path.Combine(CaseDirectory, "http-status.json"), new { status = (int)response.StatusCode });
            return response;
        }
        private void SaveSafe(string file, string body)
        {
            string Safe(string text) => text.Replace(key, "[REDACTED]", StringComparison.Ordinal)
                .Replace(protectedKey, "[REDACTED]", StringComparison.Ordinal);
            JsonNode? Filter(JsonNode? node)
            {
                if (node is JsonObject obj)
                {
                    foreach (string name in obj.Select(p => p.Key).ToArray())
                    {
                        string normalized = name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
                        if (normalized.Contains("account", StringComparison.Ordinal) || normalized is "authorization" or "apikey" or "protectedkey" or "key") obj.Remove(name);
                        else Filter(obj[name]);
                    }
                }
                else if (node is JsonArray array) for (int i = 0; i < array.Count; i++) Filter(array[i]);
                return node;
            }
            string safe = Safe(body);
            try { safe = Filter(JsonNode.Parse(safe))?.ToJsonString(OutputJson) ?? "null"; }
            catch (JsonException) { }
            File.WriteAllText(Path.Combine(CaseDirectory, file), safe, new UTF8Encoding(false));
        }
    }
}
