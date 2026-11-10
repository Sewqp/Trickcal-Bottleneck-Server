using System.Text;
using GameServer.Config;
using GameServer.DB;
using GameServer.DB.Repository;
using GameServer.Grains;
using GameServer.Network;

namespace GameServer.Packet.Handler;

public static class LoginHandler
{
    private const int LoginLogInterval = 1000;
    private static readonly TimeSpan ReconnectTokenTtl = TimeSpan.FromSeconds(300);
    private static long _loginCount;

    public static async Task HandleAsync(ClientSession session, Memory<byte> packet)
    {
        if (packet.Length < PacketHeader.HeaderSize + 2) { Apm.Agent.LocalTelemetry.SetOutcome("rejected", "malformed_payload"); return; }
        var payload = packet.Span[PacketHeader.HeaderSize..];

        ushort nameLen = BitConverter.ToUInt16(payload);
        if (payload.Length < 2 + nameLen) { Apm.Agent.LocalTelemetry.SetOutcome("rejected", "malformed_payload"); return; }
        var name = Encoding.UTF8.GetString(payload.Slice(2, nameLen));

        var (playerId, isNewPlayer) = await PlayerRepository.Instance.GetOrCreateByNameAsync(name);
        await session.AttachPlayerAsync(playerId);

        // 로그인 기믹: 이름이 "admin"으로 시작하면 세션에 어드민 플래그가 붙음(테스트용, 실제 인증 아님).
        // "admin1"/"admin2"처럼 접미사를 다르게 주면 서로 다른 playerId를 쓰면서도 둘 다 어드민 혜택을
        // 받을 수 있음 — 데모에서 클라 여러 개를 동시에 admin으로 띄울 때 이름이 겹쳐서 같은 캐릭터를
        // 공유하는 문제를 피하기 위함.
        session.IsAdmin = name.StartsWith("admin", StringComparison.OrdinalIgnoreCase);

        // 데모/영상 녹화 전용 — ServerConfig.WatchPlayerName과 이름이 일치하는 딱 한 명만 PlayerWatcher가
        // 콘솔에 자세히 찍음(PacketDispatcher의 화면 전환 로그, CurrencyRepository의 엘리프 소비 로그)
        if (!string.IsNullOrEmpty(ServerConfig.Instance.WatchPlayerName) &&
            name.Equals(ServerConfig.Instance.WatchPlayerName, StringComparison.OrdinalIgnoreCase))
        {
            PlayerWatcher.SetWatched(playerId);
            Console.WriteLine($"[WATCH pid={playerId}] '{name}' 로그인 — 이 플레이어를 추적합니다.");
        }

        // 이미 길드에 속해 있다면 그 길드에 접속 상태를 알림(길드 자체를 새로 만들거나 가입할 때뿐
        // 아니라, 로그인/재접속으로 실제 온라인이 될 때마다 알려야 정확함)
        var guildId = await GuildRepository.Instance.GetGuildIdByPlayerAsync(playerId);
        if (guildId.HasValue)
            await OrleansClient.Instance.Factory.GetGrain<IGuildGrain>(guildId.Value).MemberOnlineAsync(playerId, session.SessionId);

        var token = Guid.NewGuid().ToString();
        await RedisClient.Instance.Db.StringSetAsync($"reconnect:{token}", playerId, ReconnectTokenTtl);

        // payload: [1B success][8B playerId][36B token]
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var result = new byte[45];
        result[0] = 1;
        BitConverter.TryWriteBytes(result.AsSpan(1), playerId);
        tokenBytes.CopyTo(result.AsSpan(9));

        await session.SendAsync(PacketWriter.Build(PacketId.LoginResult, result));

        long loginCount = Interlocked.Increment(ref _loginCount);
        if (loginCount % LoginLogInterval == 0)
            Console.WriteLine($"[Login] Total logins so far: {loginCount}");
    }
}
