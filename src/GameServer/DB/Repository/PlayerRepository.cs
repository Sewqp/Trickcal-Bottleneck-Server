using Npgsql;
using GameServer.DB.Model;

namespace GameServer.DB.Repository;

public sealed class PlayerRepository
{
    public static readonly PlayerRepository Instance = new();
    private PlayerRepository() { }

    public async Task<PlayerModel?> GetByIdAsync(long playerId)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT player_id, pname, status, created_at, updated_at " +
            "FROM player WHERE player_id = @id";
        cmd.Parameters.AddWithValue("@id", playerId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return ReadPlayer(reader);
    }

    public async Task<PlayerModel?> GetByNameAsync(string name)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT player_id, pname, status, created_at, updated_at " +
            "FROM player WHERE pname = @name";
        cmd.Parameters.AddWithValue("@name", name);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return ReadPlayer(reader);
    }

    public async Task<long> CreateAsync(string name)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO player (pname) VALUES (@name) RETURNING player_id";
        cmd.Parameters.AddWithValue("@name", name);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // 계정 생성 시 character_status 전체 캐릭터분을 같은 트랜잭션에서 bulk insert해야 하므로,
    // xmax = 0 트릭으로 "방금 INSERT됐는지 vs 이미 있던 row라 UPDATE만 됐는지"를 구분함
    // (신규 생성일 때만 bulk insert 실행 — 기존 플레이어 재로그인 시 중복 생성 방지)
    public async Task<long> GetOrCreateByNameAsync(string name)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        long playerId;
        bool inserted;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO player (pname) VALUES (@name)
                ON CONFLICT (pname) DO UPDATE SET pname = EXCLUDED.pname
                RETURNING player_id, (xmax = 0) AS inserted
                """;
            cmd.Parameters.AddWithValue("@name", name);

            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            playerId = reader.GetInt64(0);
            inserted = reader.GetBoolean(1);
        }

        if (inserted)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO character_status (player_id, character_id, status, level)
                SELECT @pid, character_id, 0, 1
                FROM character_info
                """;
            cmd.Parameters.AddWithValue("@pid", playerId);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return playerId;
    }

    private static PlayerModel ReadPlayer(NpgsqlDataReader reader) => new()
    {
        PlayerId  = reader.GetInt64(0),
        PName     = reader.GetString(1),
        Status    = (byte)reader.GetInt16(2),
        CreatedAt = reader.GetDateTime(3),
        UpdatedAt = reader.GetDateTime(4),
    };
}
