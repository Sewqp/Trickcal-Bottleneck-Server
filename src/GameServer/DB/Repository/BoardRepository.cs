using StackExchange.Redis;
using Npgsql;
using GameServer.DB.Model;

namespace GameServer.DB.Repository;

// 보드 해금 상태는 redis가 기준값, PG는 flush 시점 스냅샷 — player_stat/player_currency와 동일 원칙
// (2026-08-08 재설계: 처음엔 SQL 즉시 반영이었으나, 캐릭터 신규 "획득"과 달리 보드 "해금"은 레벨업처럼
// 진행 중 자주 바뀌는 값이라 redis 델타 방식이 더 맞다고 확정됨). 플레이어 1명의 보드 해금 상태 전체를
// 필드 "{characterId}:{boardNo}" → 0/1 값으로 묶은 redis Hash 하나에 담음(개인/전체 보드 각각 별도
// Hash — 테이블 분리 원칙과 동일하게 redis 키도 분리). 비용 검증(어떤 재화로 얼마)은 Handler 레이어 몫.
public sealed class BoardRepository
{
    public static readonly BoardRepository Instance = new();
    private BoardRepository() { }

    private static string CharBoardKey(long playerId) => $"player:cboard:{playerId}";
    private static string GlobalBoardKey(long playerId) => $"player:gboard:{playerId}";
    private static string Field(int characterId, int boardNo) => $"{characterId}:{boardNo}";

    // 마스터(character_board/global_board) cost 조회 — 해금 시 비용 검증용. 정적 참조 데이터라
    // redis 캐싱 없이 매번 직접 SELECT(이 규모에서 캐싱 안 해도 부담 없음).
    public async Task<CharacterBoardModel?> GetCharacterBoardMasterAsync(int characterId, int boardNo)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT character_id, board_no, cost, stat_type, stat_value " +
            "FROM character_board WHERE character_id = @cid AND board_no = @no";
        cmd.Parameters.AddWithValue("@cid", characterId);
        cmd.Parameters.AddWithValue("@no", boardNo);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new CharacterBoardModel
        {
            CharacterId = reader.GetInt32(0),
            BoardNo = reader.GetInt32(1),
            Cost = reader.GetInt32(2),
            StatType = (byte)reader.GetInt16(3),
            StatValue = reader.GetInt32(4),
        };
    }

    public async Task<GlobalBoardModel?> GetGlobalBoardMasterAsync(int characterId, int boardNo)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT character_id, board_no, cost, stat_type, stat_value " +
            "FROM global_board WHERE character_id = @cid AND board_no = @no";
        cmd.Parameters.AddWithValue("@cid", characterId);
        cmd.Parameters.AddWithValue("@no", boardNo);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new GlobalBoardModel
        {
            CharacterId = reader.GetInt32(0),
            BoardNo = reader.GetInt32(1),
            Cost = reader.GetInt32(2),
            StatType = (byte)reader.GetInt16(3),
            StatValue = reader.GetInt32(4),
        };
    }

    public async Task<List<PlayerCharacterBoardModel>> GetPlayerCharacterBoardsAsync(long playerId, int characterId)
    {
        var entries = await GetOrLoadCharacterBoardsAsync(playerId);
        var result = new List<PlayerCharacterBoardModel>();
        foreach (var e in entries)
        {
            var (cid, boardNo) = ParseField(e.Name!);
            if (cid != characterId) continue;
            result.Add(new PlayerCharacterBoardModel
            {
                PlayerId = playerId,
                CharacterId = cid,
                BoardNo = boardNo,
                Status = (byte)(long)e.Value,
            });
        }
        result.Sort((a, b) => a.BoardNo.CompareTo(b.BoardNo));
        return result;
    }

    // 배치된 캐릭터 무관하게 계정이 보유한 전체 global_board 해금 상태를 가져옴
    public async Task<List<PlayerGlobalBoardModel>> GetPlayerGlobalBoardsAsync(long playerId)
    {
        var entries = await GetOrLoadGlobalBoardsAsync(playerId);
        var result = new List<PlayerGlobalBoardModel>();
        foreach (var e in entries)
        {
            var (cid, boardNo) = ParseField(e.Name!);
            result.Add(new PlayerGlobalBoardModel
            {
                PlayerId = playerId,
                CharacterId = cid,
                BoardNo = boardNo,
                Status = (byte)(long)e.Value,
            });
        }
        result.Sort((a, b) => a.CharacterId != b.CharacterId
            ? a.CharacterId.CompareTo(b.CharacterId)
            : a.BoardNo.CompareTo(b.BoardNo));
        return result;
    }

    // 비용 검증(재화 차감)은 이미 Handler에서 끝났다는 전제 — 여기선 상태만 1로 세팅
    public async Task UnlockCharacterBoardAsync(long playerId, int characterId, int boardNo)
    {
        await GetOrLoadCharacterBoardsAsync(playerId); // 캐시 프라이밍 보장
        await DB.RedisClient.Instance.Db.HashSetAsync(CharBoardKey(playerId), Field(characterId, boardNo), 1);
    }

    public async Task UnlockGlobalBoardAsync(long playerId, int characterId, int boardNo)
    {
        await GetOrLoadGlobalBoardsAsync(playerId);
        await DB.RedisClient.Instance.Db.HashSetAsync(GlobalBoardKey(playerId), Field(characterId, boardNo), 1);
    }

    public async Task FlushToDbAsync(long playerId)
    {
        var db = DB.RedisClient.Instance.Db;
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();

        var cEntries = await db.HashGetAllAsync(CharBoardKey(playerId));
        if (cEntries.Length > 0)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO player_character_board (player_id, character_id, board_no, status)
                SELECT @pid, unnest(@cids), unnest(@nos), unnest(@statuses)
                ON CONFLICT (player_id, character_id, board_no) DO UPDATE SET status = EXCLUDED.status
                """;
            AddBulkParams(cmd, playerId, cEntries);
            await cmd.ExecuteNonQueryAsync();
        }

        var gEntries = await db.HashGetAllAsync(GlobalBoardKey(playerId));
        if (gEntries.Length > 0)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO player_global_board (player_id, character_id, board_no, status)
                SELECT @pid, unnest(@cids), unnest(@nos), unnest(@statuses)
                ON CONFLICT (player_id, character_id, board_no) DO UPDATE SET status = EXCLUDED.status
                """;
            AddBulkParams(cmd, playerId, gEntries);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static void AddBulkParams(NpgsqlCommand cmd, long playerId, HashEntry[] entries)
    {
        var characterIds = new int[entries.Length];
        var boardNos = new int[entries.Length];
        var statuses = new short[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            var (cid, boardNo) = ParseField(entries[i].Name!);
            characterIds[i] = cid;
            boardNos[i] = boardNo;
            statuses[i] = (short)(long)entries[i].Value;
        }

        cmd.Parameters.AddWithValue("@pid", playerId);
        cmd.Parameters.AddWithValue("@cids", characterIds);
        cmd.Parameters.AddWithValue("@nos", boardNos);
        cmd.Parameters.AddWithValue("@statuses", statuses);
    }

    private static (int CharacterId, int BoardNo) ParseField(string field)
    {
        var parts = field.Split(':');
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    // 캐시 미스 시 이 플레이어의 개인 보드 해금 상태 전체를 한 번에 로드 — 캐릭터별로 쪼개서
    // 부분 로드하지 않음(계정 규모상 전체를 한 번에 캐싱해도 부담 없음, currency와 동일 원칙)
    private async Task<HashEntry[]> GetOrLoadCharacterBoardsAsync(long playerId)
    {
        var db = DB.RedisClient.Instance.Db;
        var key = CharBoardKey(playerId);
        var entries = await db.HashGetAllAsync(key);
        if (entries.Length > 0) return entries;

        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT character_id, board_no, status FROM player_character_board WHERE player_id = @pid";
        cmd.Parameters.AddWithValue("@pid", playerId);

        var loaded = new List<HashEntry>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                loaded.Add(new HashEntry(Field(reader.GetInt32(0), reader.GetInt32(1)), reader.GetInt16(2)));
        }
        if (loaded.Count == 0) return Array.Empty<HashEntry>(); // 아직 캐릭터 미보유 — 다음에 다시 시도

        var hashEntries = loaded.ToArray();
        await db.HashSetAsync(key, hashEntries);
        return hashEntries;
    }

    private async Task<HashEntry[]> GetOrLoadGlobalBoardsAsync(long playerId)
    {
        var db = DB.RedisClient.Instance.Db;
        var key = GlobalBoardKey(playerId);
        var entries = await db.HashGetAllAsync(key);
        if (entries.Length > 0) return entries;

        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT character_id, board_no, status FROM player_global_board WHERE player_id = @pid";
        cmd.Parameters.AddWithValue("@pid", playerId);

        var loaded = new List<HashEntry>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                loaded.Add(new HashEntry(Field(reader.GetInt32(0), reader.GetInt32(1)), reader.GetInt16(2)));
        }
        if (loaded.Count == 0) return Array.Empty<HashEntry>();

        var hashEntries = loaded.ToArray();
        await db.HashSetAsync(key, hashEntries);
        return hashEntries;
    }
}
