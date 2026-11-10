using System.Text.Json;
using GameServer.DB;
using GameServer.DB.Repository;
using StackExchange.Redis;
using static GameServer.DB.CombatPowerCalculator;

namespace GameServer.Progress;

public static class ProgressRepository
{
    public const string DirtyPlayers = "trickcal:dirty_players";
    public static string PendingKey(long playerId) => $"player:gacha:pending:{playerId}";
    public static string ReceiptKey(long playerId, Guid requestId) => $"player:gacha:receipt:{playerId}:{requestId:N}";
    private static IDatabase Database => RedisClient.Instance.Db;

    public static async Task<ProgressState> LoadAsync(long playerId)
    {
        var state = new ProgressState();
        await LoadDefinitionsAsync(state);
        foreach (var character in await CharacterRepository.Instance.GetStatusListAsync(playerId))
        {
            if (character.Status != 1) continue;
            state.Levels[character.CharacterId] = character.Level;
            foreach (var board in await BoardRepository.Instance.GetPlayerCharacterBoardsAsync(playerId, character.CharacterId))
                if (board.Status == 1) state.UnlockedPersonal.Add(ProgressState.BoardField(board.CharacterId, board.BoardNo));
        }
        foreach (var board in await BoardRepository.Instance.GetPlayerGlobalBoardsAsync(playerId))
            if (board.Status == 1) state.UnlockedGlobal.Add(ProgressState.BoardField(board.CharacterId, board.BoardNo));
        await CurrencyRepository.Instance.GetAsync(playerId);
        // 이전 버전의 중복/반올림 오차를 파생 데이터인 전투력에서 바로잡는다.
        await Database.ScriptEvaluateAsync("""
            redis.call('SET', KEYS[1], ARGV[1])
            redis.call('SADD', KEYS[2], ARGV[2])
            return 1
            """, [PlayerStatRepository.GetRedisKey(playerId), DirtyPlayers], [state.CalculateTotal(), playerId]);
        return state;
    }

    private static async Task LoadDefinitionsAsync(ProgressState state)
    {
        await using var connection = DbConnectionPool.Instance.GetConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT character_id, base_stat, stat_growth_table FROM character_info;
            SELECT character_id, board_no, cost, stat_type, stat_value, false FROM character_board
            UNION ALL SELECT character_id, board_no, cost, stat_type, stat_value, true FROM global_board;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            state.Characters.Add(reader.GetInt32(0), new(reader.GetInt32(0),
                StatVector.Parse(reader.GetString(1)), StatVector.Parse(reader.GetString(2))));
        await reader.NextResultAsync();
        while (await reader.ReadAsync())
        {
            var board = new BoardDefinition(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                StatVector.FromDelta(reader.GetInt16(3), reader.GetInt32(4)));
            var boards = reader.GetBoolean(5) ? state.GlobalBoards : state.PersonalBoards;
            boards.Add(ProgressState.BoardField(board.CharacterId, board.Number), board);
        }
    }

    // Lua에서는 검증을 전부 먼저 한다. 재화·진행·전투력·재시도 표식을 같은 Redis 실행에서 변경한다.
    public static async Task<bool> UnlockAsync(long playerId, string field, bool global, long cost, long delta)
    {
        var result = await Database.ScriptEvaluateAsync("""
            if redis.call('HGET', KEYS[2], ARGV[1]) == '1' then return 0 end
            if tonumber(redis.call('HGET', KEYS[1], 'gold') or '0') < tonumber(ARGV[2]) then return 0 end
            redis.call('HINCRBY', KEYS[1], 'gold', -tonumber(ARGV[2]))
            redis.call('HSET', KEYS[2], ARGV[1], 1)
            redis.call('INCRBY', KEYS[3], ARGV[3])
            redis.call('SADD', KEYS[4], ARGV[4])
            return 1
            """, [CurrencyRepository.GetRedisKey(playerId),
                $"player:{(global ? "gboard" : "cboard")}:{playerId}",
                PlayerStatRepository.GetRedisKey(playerId), DirtyPlayers],
            [field, cost, delta, playerId]);
        return (long)result == 1;
    }

    public static async Task<bool> EnhanceAsync(long playerId, int characterId, int fromLevel, int toLevel,
        long gold, long macaron, long delta)
    {
        var result = await Database.ScriptEvaluateAsync("""
            local level = tonumber(redis.call('HGET', KEYS[2], ARGV[1]) or ARGV[2])
            if level ~= tonumber(ARGV[2]) then return 0 end
            if tonumber(redis.call('HGET', KEYS[1], 'gold') or '0') < tonumber(ARGV[4]) then return 0 end
            if tonumber(redis.call('HGET', KEYS[1], 'macaron') or '0') < tonumber(ARGV[5]) then return 0 end
            redis.call('HINCRBY', KEYS[1], 'gold', -tonumber(ARGV[4]))
            redis.call('HINCRBY', KEYS[1], 'macaron', -tonumber(ARGV[5]))
            redis.call('HSET', KEYS[2], ARGV[1], ARGV[3])
            redis.call('INCRBY', KEYS[3], ARGV[6])
            redis.call('SADD', KEYS[4], ARGV[7])
            return 1
            """, [CurrencyRepository.GetRedisKey(playerId), $"player:charlevel:{playerId}",
                PlayerStatRepository.GetRedisKey(playerId), DirtyPlayers],
            [characterId, fromLevel, toLevel, gold, macaron, delta, playerId]);
        return (long)result == 1;
    }

    public static async Task<bool> BeginGachaAsync(long playerId, GachaJournal journal)
    {
        var result = await Database.ScriptEvaluateAsync("""
            if redis.call('EXISTS', KEYS[2]) == 1 or redis.call('EXISTS', KEYS[4]) == 1 then return 0 end
            if tonumber(redis.call('HGET', KEYS[1], 'elleaf') or '0') < 1000 then return 0 end
            redis.call('HINCRBY', KEYS[1], 'elleaf', -1000)
            redis.call('SET', KEYS[2], ARGV[1])
            redis.call('SADD', KEYS[3], ARGV[2])
            return 1
            """, [CurrencyRepository.GetRedisKey(playerId), PendingKey(playerId), DirtyPlayers,
                ReceiptKey(playerId, journal.RequestId)], [JsonSerializer.Serialize(journal), playerId]);
        return (long)result == 1;
    }

    public static async Task<bool> RecoverGachaAsync(long playerId)
    {
        var pending = await Database.StringGetAsync(PendingKey(playerId));
        if (!pending.HasValue) return false;
        var journal = JsonSerializer.Deserialize<GachaJournal>((string)pending!)!;
        // SQL 획득은 멱등이다. SQL 커밋 후 죽어도 저장한 추첨 계획으로 Redis 반영만 이어간다.
        await CharacterRepository.Instance.AcquireCharactersAsync(playerId, journal.NewIds);
        GachaFaults.ThrowIfEnabled("after-sql");
        var result = await Database.ScriptEvaluateAsync("""
            if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
            redis.call('HINCRBY', KEYS[2], 'elleaf', ARGV[2])
            redis.call('INCRBY', KEYS[3], ARGV[3])
            local levels = cjson.decode(ARGV[4])
            for _, id in ipairs(levels) do redis.call('HSETNX', KEYS[4], tostring(id), 1) end
            for _, field in ipairs(cjson.decode(ARGV[5])) do redis.call('HSETNX', KEYS[5], field, 0) end
            for _, field in ipairs(cjson.decode(ARGV[6])) do redis.call('HSETNX', KEYS[6], field, 0) end
            redis.call('SET', KEYS[7], ARGV[7], 'EX', 604800)
            redis.call('DEL', KEYS[1])
            redis.call('SADD', KEYS[8], ARGV[8])
            return 1
            """, [PendingKey(playerId), CurrencyRepository.GetRedisKey(playerId),
                PlayerStatRepository.GetRedisKey(playerId), $"player:charlevel:{playerId}",
                $"player:cboard:{playerId}", $"player:gboard:{playerId}",
                ReceiptKey(playerId, journal.RequestId), DirtyPlayers],
            [pending, journal.Compensation, journal.PowerDelta, JsonSerializer.Serialize(journal.NewIds),
                JsonSerializer.Serialize(journal.PersonalFields), JsonSerializer.Serialize(journal.GlobalFields),
                Convert.ToBase64String(journal.Response), playerId]);
        return (long)result == 1;
    }
}

public sealed record GachaJournal(Guid RequestId, int[] NewIds, long Compensation, long PowerDelta,
    string[] PersonalFields, string[] GlobalFields, byte[] Response);
