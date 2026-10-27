using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MinorShift.Emuera.NativeAI;

// Run only through the explicit paid-test switch. The normal test suite never calls this.
internal static class SemanticDialogueApiProbe
{
    private static readonly JsonSerializerOptions OutputJson = new(InventoryState.Json) { WriteIndented = true };
    private const int ClothId = 3;
    private const int WoundId = 7;

    internal static async Task Run(string settingsDirectory, string outputDirectory)
    {
        string configuration = Path.GetFullPath(settingsDirectory), output = Path.GetFullPath(outputDirectory);
        string gameRoot = Path.GetDirectoryName(configuration) ?? configuration;
        if (Inside(output, gameRoot) || Inside(configuration, output))
            throw new ArgumentException("测试输出须与游戏和配置目录分开。");
        var settings = new AiSettingsStore(configuration).Load();
        if (settings.Mode != "api") throw new ArgumentException("请先在游戏设置网络API；本测试不会改设置。");
        settings.Validate();
        string apiKey = settings.GetKey();
        void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, OutputJson)
            .Replace(apiKey, "[REDACTED]", StringComparison.Ordinal)
            .Replace(settings.ProtectedKey, "[REDACTED]", StringComparison.Ordinal), new UTF8Encoding(false));
        var scenario = ScenarioFixture.Ella;
        var generation = new GenerationSettingsStore(configuration).Load(settings) with
            { OutputTokens = 4096, ContextTokens = 0, HistoryTurns = 0, ResponseLength = "long" };
        generation.Validate();
        Directory.CreateDirectory(output);
        using (var guard = new FileStream(Path.Combine(output, "paid-started.txt"), FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(guard, new UTF8Encoding(false)))
            writer.Write("Maximum 3 paid HTTP requests. Never retry or reuse this output directory automatically.");

        var conversation = new ConversationStore(Path.Combine(output, "history"));
        var usage = new AccountUsageStore(Path.Combine(output, "usage-private"));
        using var capture = new CaptureHandler(output, apiKey, settings.ProtectedKey);
        using var http = new HttpClient(capture) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new AiClient(http, conversation, usage);
        var reports = new List<object>();
        Write(Path.Combine(output, "metadata.json"), new { model = settings.Model, maximum_requests = 3,
            started_at = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(8)), retries = 0, scenario = scenario.Id,
            purpose = "自然语言指代伤势、询问与执行区分、统一疲劳和否定假设语义" });

        InventoryState Fresh(bool withWound)
        {
            var state = new InventoryState();
            state.BindOwners(scenario.Owners); state.BindCharacters(scenario.Characters);
            state.BindWorld(scenario); state.SetScene(scenario.InitialScene);
            state.AddInitial(1, "铜钥匙", "用于打开小屋门锁的铜钥匙。", "普通物品", "材质：铜", 2, "player", "");
            state.AddInitial(2, "食材", "可烹煮日常饭食的食材。", "消耗品", "用途：做饭", 10, "player", "");
            state.AddInitial(ClothId, "干净布条", "洗净的浅色亚麻布条，可用于包扎。", "消耗品", "用途：包扎", 2, "player", "");
            if (withWound)
            {
                state.Statuses.Add(new() { Id = WoundId, Character = "player", Name = "左手食指浅伤", Category = "伤势", Severity = "轻微",
                    Description = "左手食指侧面被木刺划出一道浅口，木刺已挑出，仍有轻微疼痛，尚未包扎、未愈合。",
                    Effects = "碰触时轻微刺痛。", CreatedAt = "第2天 时间1/8", UpdatedAt = "第2天 时间1/8" });
                state.NextStatusId = WoundId + 1;
            }
            return InventoryState.Read(state.Snapshot());
        }

        // This check only builds an in-memory prompt. It adds no paid request and
        // verifies that reducing a secondary portrait never clips current wounds.
        var minorFixture = Fresh(withWound: false);
        minorFixture.Characters["npc_1"] = minorFixture.Owners["npc_1"] = "罗莎";
        string archivedPortrait = "罗莎是村落集市的盐商，戴着蓝围巾，说话干脆。" + string.Concat(Enumerable.Repeat("她认真清点货物，并记得熟客的习惯。", 30));
        minorFixture.DynamicCharacters["npc_1"] = new() { Name = "罗莎", Description = archivedPortrait, CreatedAt = "第1天" };
        minorFixture.NextCharacterId = 2;
        minorFixture.CharacterLocations["npc_1"] = minorFixture.Scene!.Location;
        minorFixture.Scene.PresentCharacters.Add("npc_1");
        string archivedWound = "左脚扭伤尚未恢复，行走时疼痛。" + string.Concat(Enumerable.Repeat("包扎仍在，需要避免负重。", 20));
        minorFixture.Statuses.Add(new() { Id = 1, Character = "npc_1", Name = "左脚扭伤", Category = "伤势", Severity = "中度",
            Description = archivedWound, Effects = "行走较慢，不能长时间负重。", CreatedAt = "第1天", UpdatedAt = "第2天" });
        minorFixture.NextStatusId = 2;
        var localRequest = new AiRequest("本轮资料", "问罗莎脚还疼不疼", "semantic_minor_fixture", "第2天", 4096, 0, "",
            Inventory: new(minorFixture.Snapshot(), "minor-prompt-local"));
        using (var prompt = JsonDocument.Parse(PromptBuilder.User(localRequest, [])))
        {
            var context = prompt.RootElement.GetProperty("program_context");
            var cards = context.GetProperty("character_cards").EnumerateArray().Where(card => card.GetProperty("id").GetString() == "npc_1").ToArray();
            var wounds = context.GetProperty("statuses").GetProperty("items").EnumerateArray().Where(status => status.GetProperty("character").GetString() == "npc_1").ToArray();
            Write(Path.Combine(output, "local-prompt-checks.json"), new
            {
                secondary_portrait_at_most_120_characters = cards.Length == 1 && cards[0].GetProperty("description").GetString()!.Length <= 120,
                secondary_current_wound_complete = wounds.Length == 1 && wounds[0].GetProperty("description").GetString() == archivedWound,
                full_portrait_still_saved_locally = minorFixture.DynamicCharacters["npc_1"].Description == archivedPortrait,
                paid_http_requests = 0
            });
        }

        async Task<(AiResult Reply, string Before)> Send(int number, string name, InventoryState state,
            string player, bool selectCloth)
        {
            string directory = Path.Combine(output, $"{number:00}-{name}");
            Directory.CreateDirectory(directory); capture.CaseDirectory = directory;
            string before = state.Snapshot();
            Write(Path.Combine(directory, "before.json"), JsonNode.Parse(before)!);
            string gameTime = "第2天 时间2/8", turn = "semantic-probe-" + number + "-" + Guid.NewGuid().ToString("N");
            string facts = scenario.ContentValue("prompt_core") + "\n主角：" + scenario.ContentValue("player") +
                "\n艾拉是7岁的红发雀斑女孩，性格开朗。\n" + scenario.ContentValue("rules");
            var exchange = new InventoryExchange(before, turn, ScenarioId: scenario.Id,
                Instruction: scenario.InventoryInstruction, GameTime: gameTime, SelectionRules: scenario.ContextSelection,
                SelectedItems: selectCloth ? [ClothId] : [],
                SelectedAmounts: selectCloth ? new Dictionary<int, int> { [ClothId] = 1 } : null);
            var request = new AiRequest(PromptBuilder.Material(facts, gameTime, scenario.InventoryInstruction), player,
                "semantic_probe_" + number, gameTime, 4096, 0, "", generation, Inventory: exchange);
            AiResult reply;
            try { reply = await client.SendAsync(settings, request, CancellationToken.None); }
            catch (Exception e) { reply = new(false, "测试请求异常：" + e.GetType().Name); }
            Write(Path.Combine(directory, "result.json"), new { reply.Success, reply.Text, reply.Warning,
                reply.InventoryChanges, reply.RequiresApproval, reply.Rationale, reply.Suggestions });
            if (reply.Success && reply.InventorySnapshot.Length > 0)
                Write(Path.Combine(directory, "candidate.json"), JsonNode.Parse(reply.InventorySnapshot)!);
            return (reply, before);
        }

        void Report(int number, string name, AiResult reply, Dictionary<string, bool> checks, object? detail = null)
        {
            reports.Add(new { round = number, name, api_success = reply.Success,
                passed = checks.Values.All(value => value), checks, detail,
                story_characters = reply.Success ? reply.Text.Length : 0, warning = reply.Warning });
            Write(Path.Combine(output, "results.json"), reports);
            Console.WriteLine($"语义复测 {number}/3 {name}：API{(reply.Success ? "成功" : "未采用")}；检查{checks.Values.Count(value => value)}/{checks.Count}通过");
        }

        var inquiry = Fresh(withWound: true);
        var (r1, b1) = await Send(1, "treatment-inquiry", inquiry,
            "我把刚才破的那个地方给艾拉看看，问她该怎么处理。我只是问一下，暂不动手，也没有使用选中的布条。", selectCloth: true);
        var checks1 = new Dictionary<string, bool> { ["api_valid"] = r1.Success, ["unadopted_source_unchanged"] = inquiry.Snapshot() == b1 };
        if (r1.Success)
        {
            var candidate = InventoryState.Read(r1.InventorySnapshot);
            checks1["selected_material_not_consumed_by_question"] = JsonSerializer.Serialize(candidate.Items, InventoryState.Json) == JsonSerializer.Serialize(inquiry.Items, InventoryState.Json);
            checks1["original_wound_unchanged"] = JsonSerializer.Serialize(candidate.Statuses, InventoryState.Json) == JsonSerializer.Serialize(inquiry.Statuses, InventoryState.Json);
            checks1["same_wound_id_no_duplicate"] = candidate.Statuses.Count == 1 && candidate.Statuses.Single().Id == WoundId;
            checks1["no_new_fatigue_from_question"] = candidate.Statuses.All(status => status.Category != "疲劳");
        }
        Report(1, "口语询问治疗不执行", r1, checks1);

        var tired = Fresh(withWound: false);
        var (r2, b2) = await Send(2, "fatigue-paraphrase", tired,
            "我刚把一大堆木柴扛进小屋，胳膊酸得发沉，两条腿也没什么劲，只想坐着缓一缓。我还没有开始休息。", selectCloth: false);
        var checks2 = new Dictionary<string, bool> { ["api_valid"] = r2.Success, ["unadopted_source_unchanged"] = tired.Snapshot() == b2 };
        if (r2.Success)
        {
            var candidate = InventoryState.Read(r2.InventorySnapshot);
            var fatigue = candidate.Statuses.Where(status => status.Character == "player" && status.Category == "疲劳").ToArray();
            checks2["natural_description_creates_one_canonical_fatigue"] = fatigue.Length == 1 && fatigue[0].Name == "疲劳";
            checks2["fatigue_has_known_severity"] = fatigue.Length == 1 && fatigue[0].Severity is "轻微" or "轻度" or "中度" or "重度";
            checks2["no_invented_injury_or_disease"] = candidate.Statuses.All(status => status.Category == "疲劳");
            checks2["no_material_consumption"] = tired.Items.All(item => candidate.Items.Any(current => current.Id == item.Id &&
                JsonSerializer.Serialize(current.Quantities, InventoryState.Json) == JsonSerializer.Serialize(item.Quantities, InventoryState.Json)));
            checks2["proposed_acquisitions_require_approval"] = candidate.Items.Count == tired.Items.Count || r2.RequiresApproval;
        }
        Report(2, "自然说法统一疲劳", r2, checks2);

        // This independent fixture is already free of fatigue; it does not claim that
        // a model can perform the local rest action merely by describing a past rest.
        var treatment = Fresh(withWound: true);
        var (r3, b3) = await Send(3, "negation-and-treatment", treatment,
            "我现在并不累，也不困。如果明天累了再休息，那是明天的事。现在我拿选中的一条干净布条，把刚才破的那个口子包起来；只用这一条，不动另一条，包好之后伤口还没长好。", selectCloth: true);
        var checks3 = new Dictionary<string, bool> { ["api_valid"] = r3.Success, ["unadopted_source_unchanged"] = treatment.Snapshot() == b3 };
        if (r3.Success)
        {
            var candidate = InventoryState.Read(r3.InventorySnapshot);
            var wound = candidate.Statuses.SingleOrDefault(status => status.Id == WoundId);
            checks3["negation_and_future_condition_create_no_fatigue"] = candidate.Statuses.All(status => status.Category != "疲劳");
            checks3["exactly_one_selected_cloth_consumed"] = candidate.Item(ClothId).Quantities.GetValueOrDefault("player") == 1;
            checks3["same_wound_id_and_origin_retained"] = wound != null && wound.Character == "player" && wound.CreatedAt == treatment.Status(WoundId).CreatedAt;
            checks3["treatment_updates_original_wound"] = wound != null && wound.Description != treatment.Status(WoundId).Description && wound.UpdatedAt != treatment.Status(WoundId).UpdatedAt;
            checks3["wound_not_removed_or_duplicated"] = candidate.Statuses.Count(status => status.Character == "player" && status.Category == "伤势") == 1 && wound != null;
            checks3["other_inventory_unchanged"] = candidate.Items.Count == treatment.Items.Count && treatment.Items.Where(item => item.Id != ClothId)
                .All(item => candidate.Items.Any(other => other.Id == item.Id &&
                    JsonSerializer.Serialize(item, InventoryState.Json) == JsonSerializer.Serialize(other, InventoryState.Json)));
        }
        Report(3, "否定假设与指代包扎", r3, checks3);
        var dayUsage = usage.Read(settings);
        Write(Path.Combine(output, "usage-sanitized.json"), new { dayUsage.Date, dayUsage.Input, dayUsage.Output,
            dayUsage.Requests, dayUsage.MissingUsage, dayUsage.Unpriced, dayUsage.CostMin, dayUsage.CostMax, actual_http_requests = capture.Requests });
        File.WriteAllText(Path.Combine(output, "complete.txt"), $"Completed {capture.Requests} HTTP requests; no automatic retries.", new UTF8Encoding(false));
    }

    private static bool Inside(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private sealed class CaptureHandler(string output, string key, string protectedKey) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        internal string CaseDirectory { get; set; } = output;
        internal int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++Requests > 3) throw new InvalidOperationException("语义复测最多发送3次，不得自动重试。");
            SaveSafe("request.json", await request.Content!.ReadAsStringAsync(cancellationToken));
            var response = await base.SendAsync(request, cancellationToken);
            SaveSafe("response.json", await response.Content.ReadAsStringAsync(cancellationToken));
            File.WriteAllText(Path.Combine(CaseDirectory, "http-status.json"), JsonSerializer.Serialize(new { status = (int)response.StatusCode }), new UTF8Encoding(false));
            return response;
        }
        private void SaveSafe(string name, string text)
        {
            string safe = text.Replace(key, "[REDACTED]", StringComparison.Ordinal).Replace(protectedKey, "[REDACTED]", StringComparison.Ordinal);
            void Filter(JsonNode? node)
            {
                if (node is JsonObject obj)
                {
                    foreach (string property in obj.Select(pair => pair.Key).ToArray())
                    {
                        string normalized = property.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
                        if (normalized.Contains("account", StringComparison.Ordinal) || normalized is "authorization" or "apikey" or "protectedkey" or "key") obj.Remove(property);
                        else Filter(obj[property]);
                    }
                }
                else if (node is JsonArray array) foreach (var child in array) Filter(child);
            }
            try { var json = JsonNode.Parse(safe); Filter(json); safe = json?.ToJsonString(OutputJson) ?? "null"; }
            catch (JsonException) { }
            File.WriteAllText(Path.Combine(CaseDirectory, name), safe, new UTF8Encoding(false));
        }
    }
}
