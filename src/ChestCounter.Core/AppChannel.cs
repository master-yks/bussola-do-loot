namespace ChestCounter;

public static class AppChannel
{
#if LOOT_TEST_CHANNEL
    public static bool IsTest => true;
    public const string Name = "ChestCounter-Testes";
    public const int OverlayPort = 7778;
#else
    public static bool IsTest => false;
    public const string Name = "ChestCounter";
    public const int OverlayPort = 7777;
#endif
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);
    public static string OverlayUrl => $"http://localhost:{OverlayPort}/";
}
