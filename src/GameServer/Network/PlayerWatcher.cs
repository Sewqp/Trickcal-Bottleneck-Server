namespace GameServer.Network;

// 데모/영상 녹화 전용 — ServerConfig.WatchPlayerName으로 로그인한 플레이어 딱 한 명의 행동(패킷=화면
// 전환, 엘리프 소비)만 콘솔에 자세히 찍는다. 전체 플레이어 대상으로 이런 상세 로그를 켜면 병목이지만,
// 한 명만 켜는 건 IsWatched(playerId)가 매번 nullable long 비교 하나뿐이라 사실상 공짜.
public static class PlayerWatcher
{
    private static long? _watchedPlayerId;

    public static void SetWatched(long playerId) => _watchedPlayerId = playerId;

    public static bool IsWatched(long playerId) => playerId != 0 && _watchedPlayerId == playerId;

    public static void Log(long playerId, string message)
    {
        if (IsWatched(playerId))
            Console.WriteLine($"[WATCH pid={playerId}] {message}");
    }
}
