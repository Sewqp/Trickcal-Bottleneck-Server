using Npgsql;
using GameServer.DB.Model;

namespace GameServer.DB.Repository;

// redis가 total_combat_power의 기준값(델타 누적), PG player_stat은 flush 시점 스냅샷.
// 단일 스칼라값이라 CharacterStatRepository(JSON blob + TTL)와 달리 redis STRING의
// 원자적 INCRBY를 그대로 씀 — read-modify-write 경쟁 상태 자체가 없음. TTL도 없음(휘발성
// 캐시가 아니라 상시 기준값이므로).
//
// 주의: IncrementCombatPowerAsync는 해당 플레이어의 redis 키가 이미 채워져 있다는 걸 전제함
// (세션 시작 시 GetCombatPowerAsync를 먼저 호출해 PG 값을 캐시에 로드해둘 것) — 안 그러면
// 키가 없는 상태에서 증분만 적용되어 이전 세션의 값을 잃어버림.
public sealed class PlayerStatRepository
{
    public static readonly PlayerStatRepository Instance = new();
    private PlayerStatRepository() { }

    private static string CacheKey(long playerId) => $"player:stat:{playerId}";

    public async Task<long> GetCombatPowerAsync(long playerId)
    {
        var db = DB.RedisClient.Instance.Db;
        var cached = await db.StringGetAsync(CacheKey(playerId));
        if (cached.HasValue) return (long)cached;

        long value = await FetchFromDbAsync(playerId);
        await db.StringSetAsync(CacheKey(playerId), value);
        return value;
    }

    public Task IncrementCombatPowerAsync(long playerId, long delta) =>
        DB.RedisClient.Instance.Db.StringIncrementAsync(CacheKey(playerId), delta);

    // 극장 위변조 감지 시 스냅샷 값으로 강제 복구하는 용도 — 델타 누적이 아니라 절대값 덮어쓰기
    public Task SetCombatPowerAsync(long playerId, long value) =>
        DB.RedisClient.Instance.Db.StringSetAsync(CacheKey(playerId), value);

    public async Task FlushToDbAsync(long playerId)
    {
        var db = DB.RedisClient.Instance.Db;
        var cached = await db.StringGetAsync(CacheKey(playerId));
        if (!cached.HasValue) return; // 캐시에 값이 없으면 변경분도 없는 것 — flush 불필요

        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO player_stat (player_id, total_combat_power)
            VALUES (@pid, @power)
            ON CONFLICT (player_id) DO UPDATE SET total_combat_power = EXCLUDED.total_combat_power
            """;
        cmd.Parameters.AddWithValue("@pid", playerId);
        cmd.Parameters.AddWithValue("@power", (long)cached);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> FetchFromDbAsync(long playerId)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT total_combat_power FROM player_stat WHERE player_id = @pid";
        cmd.Parameters.AddWithValue("@pid", playerId);
        var result = await cmd.ExecuteScalarAsync();
        return result is long v ? v : 0;
    }
}
