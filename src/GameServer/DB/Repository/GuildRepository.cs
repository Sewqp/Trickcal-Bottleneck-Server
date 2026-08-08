using Npgsql;
using GameServer.DB.Model;

namespace GameServer.DB.Repository;

// Game-Server-CSharp엔 테이블만 있고 실제 구현이 없었던 부분 — 이번에 신규 작성.
// 길드는 가입 이벤트 자체가 드물어서(캐릭터/보드처럼 매 프레임 바뀌는 값이 아님) redis 델타 없이
// 그냥 SQL 직접 반영으로 충분하다고 판단.
public sealed class GuildRepository
{
    public static readonly GuildRepository Instance = new();
    private GuildRepository() { }

    // 길드명 중복(uq_guild_name)이면 null 반환
    public async Task<long?> CreateAsync(string guildName, long leaderId)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        long guildId;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO guild (guild_name) VALUES (@name) RETURNING guild_id";
            cmd.Parameters.AddWithValue("@name", guildName);
            try
            {
                guildId = (long)(await cmd.ExecuteScalarAsync())!;
            }
            catch (PostgresException ex) when (ex.SqlState == "23505") // uq_guild_name 충돌
            {
                await tx.RollbackAsync();
                return null;
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO guild_member (guild_id, player_id, role) VALUES (@gid, @pid, 2)";
            cmd.Parameters.AddWithValue("@gid", guildId);
            cmd.Parameters.AddWithValue("@pid", leaderId);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return guildId;
    }

    public async Task<long?> GetGuildIdByPlayerAsync(long playerId)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT guild_id FROM guild_member WHERE player_id = @pid";
        cmd.Parameters.AddWithValue("@pid", playerId);
        var result = await cmd.ExecuteScalarAsync();
        return result is long v ? v : null;
    }

    public async Task<GuildModel?> GetByIdAsync(long guildId)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT guild_id, guild_name, guild_status, created_at FROM guild WHERE guild_id = @gid";
        cmd.Parameters.AddWithValue("@gid", guildId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new GuildModel
        {
            GuildId = reader.GetInt64(0),
            GuildName = reader.GetString(1),
            GuildStatus = (byte)reader.GetInt16(2),
            CreatedAt = reader.GetDateTime(3),
        };
    }

    public async Task<List<GuildMemberModel>> GetMembersAsync(long guildId)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT guild_id, player_id, role, joined_at FROM guild_member WHERE guild_id = @gid";
        cmd.Parameters.AddWithValue("@gid", guildId);

        var result = new List<GuildMemberModel>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new GuildMemberModel
            {
                GuildId = reader.GetInt64(0),
                PlayerId = reader.GetInt64(1),
                Role = (byte)reader.GetInt16(2),
                JoinedAt = reader.GetDateTime(3),
            });
        }
        return result;
    }

    // 이미 다른 길드에 속해 있으면(1인 1길드) 가입 실패 — NOT EXISTS로 체크와 삽입을 원자적으로 묶음
    public async Task<bool> JoinAsync(long guildId, long playerId)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO guild_member (guild_id, player_id, role)
            SELECT @gid, @pid, 0
            WHERE NOT EXISTS (SELECT 1 FROM guild_member WHERE player_id = @pid)
            """;
        cmd.Parameters.AddWithValue("@gid", guildId);
        cmd.Parameters.AddWithValue("@pid", playerId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> LeaveAsync(long guildId, long playerId)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM guild_member WHERE guild_id = @gid AND player_id = @pid";
        cmd.Parameters.AddWithValue("@gid", guildId);
        cmd.Parameters.AddWithValue("@pid", playerId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }
}
