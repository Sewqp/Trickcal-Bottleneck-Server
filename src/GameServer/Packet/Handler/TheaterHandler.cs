using StackExchange.Redis;
using GameServer.DB.Repository;
using GameServer.Grains;
using GameServer.Network;

namespace GameServer.Packet.Handler;

// 이 게임은 게임 진행 중 스토리가 나오는 게 아니라 극장에 들어가야만 스토리를 볼 수 있음 —
// 그래서 "극장 안인가(0/1)"만 확실하면 됨. 그레인 락 자체가 곧 그 0/1 플래그(진입=락, 퇴장=언락) —
// 별도 상태 필드를 안 둠. 트릭컬 스토리는 재생 중 스탯/재화 변화가 없는 컨텐츠라, 퇴장 시점 값이
// 입장 시점 스냅샷과 다르면 위변조로 간주하고 스냅샷 값으로 강제 복구함.
//
// 하드 타임아웃(EXIT이 영영 안 오는 경우 자동 락 해제) 안전장치: 대사 넘기기(터치)는 클라 로컬
// 데이터로 진행되고 서버 왕복이 없는 구조라, 터치할 때마다 TheaterTouchRequest로 생존 신호만
// 보내게 함(HandleTouchAsync, 응답 없음). HeartbeatManager가 주기적으로 마지막 터치 이후 경과
// 시간(PlayerGrain.GetLockIdleDurationAsync)을 확인해 일정 시간 넘으면 강제 종료함.
public static class TheaterHandler
{
    public static async Task HandleEnterAsync(ClientSession session, Memory<byte> packet)
    {
        var grain = OrleansClient.Instance.Factory.GetGrain<IPlayerGrain>(session.PlayerId);

        if (!await grain.LockAsync())
        {
            await session.SendAsync(PacketWriter.Build(PacketId.TheaterEnterResult, new byte[] { 0 }));
            return;
        }

        long combatPower = await PlayerStatRepository.Instance.GetCombatPowerAsync(session.PlayerId);
        var currency = await CurrencyRepository.Instance.GetAsync(session.PlayerId);

        await grain.SaveSnapshotAsync(new TheaterSnapshot
        {
            CombatPower = combatPower,
            Gold = currency.Gold,
            Elleaf = currency.Elleaf,
            Macaron = currency.Macaron,
        });

        await session.SendAsync(PacketWriter.Build(PacketId.TheaterEnterResult, new byte[] { 1 }));
    }

    public static async Task HandleExitAsync(ClientSession session, Memory<byte> packet)
    {
        var grain = OrleansClient.Instance.Factory.GetGrain<IPlayerGrain>(session.PlayerId);

        if (!await grain.IsLockedAsync())
        {
            // 극장에 들어간 적 없는데 EXIT을 보냄 — 위장 진입 시도로 보고 거부
            await session.SendAsync(PacketWriter.Build(PacketId.TheaterExitResult, new byte[] { 0 }));
            return;
        }

        var snapshot = await grain.GetSnapshotAsync();
        bool tampered = snapshot is null || await CheckAndRestoreIfTamperedAsync(session.PlayerId, snapshot);

        await grain.UnlockAsync(); // 처리 완료 신호 — 여기서 락 해제

        var final = snapshot ?? new TheaterSnapshot(); // snapshot 자체가 없는 건 그레인 상태 이상 — 방어적 처리

        // payload: [1B tampered][8B combatPower][8B gold][8B elleaf][8B macaron]
        var result = new byte[33];
        result[0] = (byte)(tampered ? 1 : 0);
        BitConverter.TryWriteBytes(result.AsSpan(1), final.CombatPower);
        BitConverter.TryWriteBytes(result.AsSpan(9), final.Gold);
        BitConverter.TryWriteBytes(result.AsSpan(17), final.Elleaf);
        BitConverter.TryWriteBytes(result.AsSpan(25), final.Macaron);
        await session.SendAsync(PacketWriter.Build(PacketId.TheaterExitResult, result));
    }

    // 대사 넘기기(터치)마다 클라가 보내는 생존 신호 — 오프라인(클라 로컬) 데이터로 진행되는 구조라
    // 서버 왕복이 원래 없는 지점에 하드 타임아웃 판정용으로만 추가한 것. 응답 없음(fire-and-forget).
    public static async Task HandleTouchAsync(ClientSession session, Memory<byte> packet)
    {
        var grain = OrleansClient.Instance.Factory.GetGrain<IPlayerGrain>(session.PlayerId);
        await grain.TouchAsync();
    }

    // 스탯(STRING) 키와 재화(HASH) 키 두 개를 하나의 Lua 스크립트로 묶어 "현재값 읽기 → 스냅샷과 비교 →
    // 다르면 즉시 덮어쓰기"를 원자적으로 처리. 예전엔 GetCombatPowerAsync/GetAsync(읽기) → C# 비교 →
    // SetCombatPowerAsync/SetAllAsync(쓰기)로 4번의 별도 redis 왕복에 걸쳐 있어서, 그 사이에 다른 재화
    // 연산이 끼어들면 lost update가 날 수 있었음(PacketDispatcher 락 게이트로 지금은 트리거 경로가
    // 없어졌지만, 새 핸들러가 락 체크를 빠뜨리는 실수를 해도 깨지지 않도록 여기서 근본적으로 막음).
    private const string CheckAndRestoreScript = """
        local curPower = tonumber(redis.call('GET', KEYS[1]) or '0')
        local curGold = tonumber(redis.call('HGET', KEYS[2], 'gold') or '0')
        local curElleaf = tonumber(redis.call('HGET', KEYS[2], 'elleaf') or '0')
        local curMacaron = tonumber(redis.call('HGET', KEYS[2], 'macaron') or '0')

        local snapPower = tonumber(ARGV[1])
        local snapGold = tonumber(ARGV[2])
        local snapElleaf = tonumber(ARGV[3])
        local snapMacaron = tonumber(ARGV[4])

        if curPower ~= snapPower or curGold ~= snapGold or curElleaf ~= snapElleaf or curMacaron ~= snapMacaron then
            redis.call('SET', KEYS[1], snapPower)
            redis.call('HSET', KEYS[2], 'gold', snapGold, 'elleaf', snapElleaf, 'macaron', snapMacaron)
            return 1
        end
        return 0
        """;

    private static async Task<bool> CheckAndRestoreIfTamperedAsync(long playerId, TheaterSnapshot snapshot)
    {
        var db = DB.RedisClient.Instance.Db;
        var result = await db.ScriptEvaluateAsync(
            CheckAndRestoreScript,
            new RedisKey[] { PlayerStatRepository.GetRedisKey(playerId), CurrencyRepository.GetRedisKey(playerId) },
            new RedisValue[] { snapshot.CombatPower, snapshot.Gold, snapshot.Elleaf, snapshot.Macaron });
        return (long)result == 1;
    }
}
