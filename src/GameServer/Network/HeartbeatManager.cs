using GameServer.DB;
using GameServer.Grains;

namespace GameServer.Network;

public sealed class HeartbeatManager
{
    private const int IntervalMs = 5_000;
    private const int TimeoutSeconds = 30;
    private const string SessionCountKey = "stats:session_count";
    private static readonly TimeSpan SessionCountTtl = TimeSpan.FromSeconds(15);

    private readonly CancellationToken _ct;

    public HeartbeatManager(CancellationToken ct) => _ct = ct;

    public async Task RunAsync()
    {
        while (!_ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(IntervalMs, _ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var cutoff = DateTime.UtcNow.AddSeconds(-TimeoutSeconds);
            var timedOut = SessionManager.Instance.GetTimedOut(cutoff);
            foreach (var session in timedOut)
            {
                // 가챠/극장 처리 중(그레인 락)이면 재접속 판정 보류 — 처리 완료 신호가 올 때까지 대기
                if (session.PlayerId != 0 &&
                    await OrleansClient.Instance.Factory.GetGrain<IPlayerGrain>(session.PlayerId).IsLockedAsync())
                    continue;

                Console.WriteLine($"[Heartbeat] Timeout: {session.SessionId}");
                await session.DisconnectAsync();
            }

            await RedisClient.Instance.Db.StringSetAsync(
                SessionCountKey, SessionManager.Instance.Count, SessionCountTtl);
        }
    }
}
