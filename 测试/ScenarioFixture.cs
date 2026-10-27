using MinorShift.Emuera.NativeAI;
internal static class ScenarioFixture
{
    internal static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../游戏/ERB/script"));
    internal static ScenarioDefinition Ella => ScenarioCatalog.Load(Root).Get("rain_room");
}
