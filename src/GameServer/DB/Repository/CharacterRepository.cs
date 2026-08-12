using StackExchange.Redis;
using Npgsql;
using GameServer.DB.Model;

namespace GameServer.DB.Repository;

public sealed class CharacterRepository
{
    public static readonly CharacterRepository Instance = new();
    private CharacterRepository() { }

    private static string LevelKey(long playerId) => $"player:charlevel:{playerId}";

    // 미보유 포함 전체 목록 — 캐릭터창에서 미보유 캐릭터도 함께 스크롤되어 보여야 하므로
    // 계정 생성 시 이미 전체 row가 깔려 있어 단일 WHERE player_id=@pid 쿼리로 충분함.
    // level은 진행 중 redis가 기준값이 될 수 있어서(강화는 redis 먼저 반영, PG는 flush 시점 스냅샷),
    // PG에서 읽은 값 위에 redis 캐시가 있으면 그걸로 덮어씀 — 세션 중 강화했는데 아직 flush 전인
    // 경우에도 최신 레벨을 보여주기 위함.
    public async Task<List<CharacterStatusModel>> GetStatusListAsync(long playerId)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT player_id, character_id, status, level, acquired_at " +
            "FROM character_status WHERE player_id = @pid";
        cmd.Parameters.AddWithValue("@pid", playerId);

        var result = new List<CharacterStatusModel>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                result.Add(new CharacterStatusModel
                {
                    PlayerId    = reader.GetInt64(0),
                    CharacterId = reader.GetInt32(1),
                    Status      = (byte)reader.GetInt16(2),
                    Level       = reader.GetInt32(3),
                    AcquiredAt  = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                });
            }
        }

        var cachedLevels = await DB.RedisClient.Instance.Db.HashGetAllAsync(LevelKey(playerId));
        if (cachedLevels.Length > 0)
        {
            var byCharacterId = cachedLevels.ToDictionary(e => (int)e.Name, e => (int)(long)e.Value);
            foreach (var status in result)
                if (byCharacterId.TryGetValue(status.CharacterId, out var level))
                    status.Level = level;
        }

        return result;
    }

    // 강화(레벨업)는 진행 상황이라 redis가 기준값 — 캐릭터 신규 "획득"(AcquireCharactersAsync, SQL
    // 즉시 반영)과는 다른 경로. 세션 시작 시 먼저 로드해둔다는 전제는 CurrencyRepository와 동일.
    public async Task<int> GetLevelAsync(long playerId, int characterId)
    {
        var entries = await GetOrLoadLevelsAsync(playerId);
        foreach (var e in entries)
            if ((int)e.Name == characterId) return (int)(long)e.Value;
        return 1; // 아직 캐시에 없고 PG에도 없으면(미보유 캐릭터 등) 기본 레벨
    }

    // 서버가 고정 테이블로 재계산해서 검증을 마친 새 레벨을 그대로 세팅(Handler 레이어가 검증 후 호출)
    public async Task SetLevelAsync(long playerId, int characterId, int newLevel)
    {
        await GetOrLoadLevelsAsync(playerId); // 캐시 프라이밍 보장
        await DB.RedisClient.Instance.Db.HashSetAsync(LevelKey(playerId), characterId, newLevel);
    }

    public async Task FlushLevelsToDbAsync(long playerId)
    {
        var entries = await DB.RedisClient.Instance.Db.HashGetAllAsync(LevelKey(playerId));
        if (entries.Length == 0) return;

        var characterIds = entries.Select(e => (int)e.Name).ToArray();
        var levels = entries.Select(e => (int)(long)e.Value).ToArray();

        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE character_status SET level = v.level
            FROM (SELECT unnest(@cids) AS character_id, unnest(@levels) AS level) AS v
            WHERE character_status.player_id = @pid AND character_status.character_id = v.character_id
            """;
        cmd.Parameters.AddWithValue("@pid", playerId);
        cmd.Parameters.AddWithValue("@cids", characterIds);
        cmd.Parameters.AddWithValue("@levels", levels);
        await cmd.ExecuteNonQueryAsync();
    }

    // 캐시 미스 시 이 플레이어가 보유한 캐릭터들의 레벨 전체를 한 번에 로드
    private async Task<HashEntry[]> GetOrLoadLevelsAsync(long playerId)
    {
        var db = DB.RedisClient.Instance.Db;
        var key = LevelKey(playerId);
        var entries = await db.HashGetAllAsync(key);
        if (entries.Length > 0) return entries;

        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT character_id, level FROM character_status WHERE player_id = @pid AND status = 1";
        cmd.Parameters.AddWithValue("@pid", playerId);

        var loaded = new List<HashEntry>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                loaded.Add(new HashEntry(reader.GetInt32(0), reader.GetInt32(1)));
        }
        if (loaded.Count == 0) return Array.Empty<HashEntry>(); // 아직 보유 캐릭터 없음 — 다음에 다시 시도

        var hashEntries = loaded.ToArray();
        await db.HashSetAsync(key, hashEntries);
        return hashEntries;
    }

    // 캐릭터 단건 획득 — 내부적으로 배치 버전을 1개짜리 리스트로 호출(트랜잭션 로직 중복 방지)
    public async Task<bool> AcquireCharacterAsync(long playerId, int characterId) =>
        (await AcquireCharactersAsync(playerId, new[] { characterId })).Count > 0;

    // 여러 캐릭터를 한 트랜잭션에서 획득 — 가챠 10연챠처럼 여러 건을 한 번에 활성화할 때
    // row별 반복 대신 IN(=ANY) 절 하나로 묶어 처리(1-4절 bulk insert 원칙과 같은 이유).
    // status=0 가드가 걸린 character_id만 실제로 활성화되고 RETURNING으로 그 목록을 받음 —
    // 이미 보유 중인 게 섞여 있어도 나머지는 정상 처리되고, 실제로 새로 획득된 것만 반환.
    public async Task<List<int>> AcquireCharactersAsync(long playerId, IReadOnlyList<int> characterIds)
    {
        if (characterIds.Count == 0) return new List<int>();

        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var acquiredIds = new List<int>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE character_status SET status = 1, acquired_at = CURRENT_TIMESTAMP
                WHERE player_id = @pid AND character_id = ANY(@ids) AND status = 0
                RETURNING character_id
                """;
            cmd.Parameters.AddWithValue("@pid", playerId);
            cmd.Parameters.AddWithValue("@ids", characterIds.ToArray());

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                acquiredIds.Add(reader.GetInt32(0));
        }

        if (acquiredIds.Count > 0)
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO player_character_board (player_id, character_id, board_no, status)
                    SELECT @pid, character_id, board_no, 0
                    FROM character_board
                    WHERE character_id = ANY(@ids)
                    """;
                cmd.Parameters.AddWithValue("@pid", playerId);
                cmd.Parameters.AddWithValue("@ids", acquiredIds.ToArray());
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO player_global_board (player_id, character_id, board_no, status)
                    SELECT @pid, character_id, board_no, 0
                    FROM global_board
                    WHERE character_id = ANY(@ids)
                    """;
                cmd.Parameters.AddWithValue("@pid", playerId);
                cmd.Parameters.AddWithValue("@ids", acquiredIds.ToArray());
                await cmd.ExecuteNonQueryAsync();
            }
        }

        await tx.CommitAsync();
        return acquiredIds;
    }

    // 전투력 계산용 — base_stat(레벨 1 기준)/stat_growth_table(레벨업 1회당 증가량) 원본 JSON 그대로 반환,
    // 파싱은 CombatPowerCalculator.StatVector.Parse가 호출부에서 담당(CharacterInfoModel 주석 참고).
    public async Task<CharacterInfoModel?> GetCharacterInfoAsync(int characterId)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT character_id, info, base_stat, stat_growth_table FROM character_info WHERE character_id = @cid";
        cmd.Parameters.AddWithValue("@cid", characterId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new CharacterInfoModel
        {
            CharacterId = reader.GetInt32(0),
            InfoJson = reader.GetString(1),
            BaseStatJson = reader.GetString(2),
            StatGrowthTableJson = reader.GetString(3),
        };
    }

    // 가챠용 — 전체 캐릭터 마스터 ID 목록(중복 허용 뽑기이므로 미보유로 미리 필터링하지 않음).
    // 독립시행(복원추출) 방식 랜덤 추첨 자체는 여기서 안 함 — 그건 순수 알고리즘이라 Handler
    // 레이어가 이 목록을 받아서 처리(Repository는 원시 데이터 접근만 담당).
    public async Task<List<int>> GetAllCharacterIdsAsync()
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT character_id FROM character_info";

        var result = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetInt32(0));
        return result;
    }
}
