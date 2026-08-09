namespace ScenarioClient;

public static class ScenarioStats
{
    private static long _loginOk;
    private static long _gachaOk;
    private static long _enhanceOk;
    private static long _boardUnlockOk;
    private static long _theaterOk;
    private static long _theaterTampered;
    private static long _guildCreateOk;
    private static long _failed;

    public static void IncrementLoginOk() => Interlocked.Increment(ref _loginOk);
    public static void IncrementGachaOk() => Interlocked.Increment(ref _gachaOk);
    public static void IncrementEnhanceOk() => Interlocked.Increment(ref _enhanceOk);
    public static void IncrementBoardUnlockOk() => Interlocked.Increment(ref _boardUnlockOk);
    public static void IncrementTheaterOk() => Interlocked.Increment(ref _theaterOk);
    public static void IncrementTheaterTampered() => Interlocked.Increment(ref _theaterTampered);
    public static void IncrementGuildCreateOk() => Interlocked.Increment(ref _guildCreateOk);
    public static void IncrementFailed() => Interlocked.Increment(ref _failed);

    public static ScenarioSnapshot Snapshot() => new(
        Interlocked.Read(ref _loginOk),
        Interlocked.Read(ref _gachaOk),
        Interlocked.Read(ref _enhanceOk),
        Interlocked.Read(ref _boardUnlockOk),
        Interlocked.Read(ref _theaterOk),
        Interlocked.Read(ref _theaterTampered),
        Interlocked.Read(ref _guildCreateOk),
        Interlocked.Read(ref _failed));
}

public readonly record struct ScenarioSnapshot(
    long LoginOk,
    long GachaOk,
    long EnhanceOk,
    long BoardUnlockOk,
    long TheaterOk,
    long TheaterTampered,
    long GuildCreateOk,
    long Failed);
