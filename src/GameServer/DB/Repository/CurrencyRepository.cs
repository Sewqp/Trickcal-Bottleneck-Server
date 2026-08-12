using StackExchange.Redis;
using Npgsql;
using GameServer.DB.Model;

namespace GameServer.DB.Repository;

// redis가 골드/엘리프/마카롱의 기준값, PG player_currency는 flush 시점 스냅샷 — player_stat과 동일 원칙.
// 세 필드를 한 지갑(redis Hash)에 묶어 HINCRBY로 필드별 원자 증감(round-trip 절약).
// 소비(가챠 등)는 잔액 확인+차감을 원자적으로 묶은 TrySpend*Async를 쓸 것 — 순수 증가(보상 지급 등)만
// Increment*Async를 직접 씀. 두 메서드 모두 PlayerStatRepository와 같은 전제:
// 세션 시작 시 GetAsync를 먼저 호출해 PG 값을 캐시에 로드해둬야 함(안 그러면 키가 없는 상태로 연산돼
// 이전 세션 잔액을 잃어버리거나, TrySpend가 실제 잔액이 있어도 0으로 오판해 실패함).
public sealed class CurrencyRepository
{
    public static readonly CurrencyRepository Instance = new();
    private CurrencyRepository() { }

    private static string CacheKey(long playerId) => $"player:currency:{playerId}";

    // TheaterHandler가 stat/currency 두 키를 하나의 Lua 스크립트로 묶어 원자적으로 다루기 위한 노출
    public static string GetRedisKey(long playerId) => CacheKey(playerId);

    public async Task<PlayerCurrencyModel> GetAsync(long playerId)
    {
        var db = DB.RedisClient.Instance.Db;
        var key = CacheKey(playerId);
        var entries = await db.HashGetAllAsync(key);
        if (entries.Length > 0) return ToModel(playerId, entries);

        var model = await FetchFromDbAsync(playerId);
        await db.HashSetAsync(key, new HashEntry[]
        {
            new("gold", model.Gold),
            new("elleaf", model.Elleaf),
            new("macaron", model.Macaron),
        });
        return model;
    }

    public Task<long> IncrementGoldAsync(long playerId, long delta) =>
        DB.RedisClient.Instance.Db.HashIncrementAsync(CacheKey(playerId), "gold", delta);

    public Task<long> IncrementElleafAsync(long playerId, long delta) =>
        DB.RedisClient.Instance.Db.HashIncrementAsync(CacheKey(playerId), "elleaf", delta);

    public Task<long> IncrementMacaronAsync(long playerId, long delta) =>
        DB.RedisClient.Instance.Db.HashIncrementAsync(CacheKey(playerId), "macaron", delta);

    // 가챠 등 "먼저 깎고 그 차감된 값을 기준으로 진행"해야 하는 소비 경로 전용 — 잔액 확인과 차감을
    // Lua 스크립트로 묶어 원자적으로 실행(HINCRBY만으로는 "0 미만 방지" 조건을 못 걺 — 중간에 다른
    // 요청이 끼어들어 잔액을 두 번 깎는 위변조/경쟁 상태를 막기 위한 조치). 잔액 부족이면 차감 없이 false.
    private const string SpendScript = """
        local current = tonumber(redis.call('HGET', KEYS[1], ARGV[1]) or '0')
        if current < tonumber(ARGV[2]) then
            return 0
        end
        redis.call('HINCRBY', KEYS[1], ARGV[1], -tonumber(ARGV[2]))
        return 1
        """;

    public Task<bool> TrySpendGoldAsync(long playerId, long amount) => TrySpendAsync(playerId, "gold", amount);

    public Task<bool> TrySpendElleafAsync(long playerId, long amount) => TrySpendAsync(playerId, "elleaf", amount);

    public Task<bool> TrySpendMacaronAsync(long playerId, long amount) => TrySpendAsync(playerId, "macaron", amount);

    private async Task<bool> TrySpendAsync(long playerId, string field, long amount)
    {
        var db = DB.RedisClient.Instance.Db;
        var result = await db.ScriptEvaluateAsync(
            SpendScript,
            new RedisKey[] { CacheKey(playerId) },
            new RedisValue[] { field, amount });
        return (long)result == 1;
    }

    public async Task FlushToDbAsync(long playerId)
    {
        var db = DB.RedisClient.Instance.Db;
        var entries = await db.HashGetAllAsync(CacheKey(playerId));
        if (entries.Length == 0) return; // 캐시에 값이 없으면 변경분도 없는 것 — flush 불필요

        var model = ToModel(playerId, entries);
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO player_currency (player_id, gold, elleaf, macaron)
            VALUES (@pid, @gold, @elleaf, @macaron)
            ON CONFLICT (player_id) DO UPDATE SET
                gold = EXCLUDED.gold,
                elleaf = EXCLUDED.elleaf,
                macaron = EXCLUDED.macaron
            """;
        cmd.Parameters.AddWithValue("@pid", playerId);
        cmd.Parameters.AddWithValue("@gold", model.Gold);
        cmd.Parameters.AddWithValue("@elleaf", model.Elleaf);
        cmd.Parameters.AddWithValue("@macaron", model.Macaron);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<PlayerCurrencyModel> FetchFromDbAsync(long playerId)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT gold, elleaf, macaron FROM player_currency WHERE player_id = @pid";
        cmd.Parameters.AddWithValue("@pid", playerId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return new PlayerCurrencyModel { PlayerId = playerId, Gold = 0, Elleaf = 0, Macaron = 0 };

        return new PlayerCurrencyModel
        {
            PlayerId = playerId,
            Gold     = reader.GetInt64(0),
            Elleaf   = reader.GetInt64(1),
            Macaron  = reader.GetInt64(2),
        };
    }

    private static PlayerCurrencyModel ToModel(long playerId, HashEntry[] entries)
    {
        var model = new PlayerCurrencyModel { PlayerId = playerId };
        foreach (var e in entries)
        {
            if (e.Name == "gold") model.Gold = (long)e.Value;
            else if (e.Name == "elleaf") model.Elleaf = (long)e.Value;
            else if (e.Name == "macaron") model.Macaron = (long)e.Value;
        }
        return model;
    }
}
