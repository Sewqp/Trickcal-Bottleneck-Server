using GameServer.Progress;
using Npgsql;
using StackExchange.Redis;

namespace GameServer.DB;

public static class SessionFlush
{
    // 호출자는 PlayerGrain이다. 진행 변경과 flush를 같은 actor에서 직렬화하며 SQL도 한 트랜잭션으로 묶는다.
    public static async Task FlushOneAsync(long playerId)
    {
        var database = RedisClient.Instance.Db;
        var currency = await database.HashGetAllAsync($"player:currency:{playerId}");
        var power = await database.StringGetAsync($"player:stat:{playerId}");
        var levels = await database.HashGetAllAsync($"player:charlevel:{playerId}");
        var personal = await database.HashGetAllAsync($"player:cboard:{playerId}");
        var global = await database.HashGetAllAsync($"player:gboard:{playerId}");
        await using var connection = DbConnectionPool.Instance.GetConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (currency.Length > 0) await SaveCurrencyAsync(connection, transaction, playerId, currency);
        if (power.HasValue) await ExecuteAsync(connection, transaction, playerId,
            "INSERT INTO player_stat VALUES (@pid, @power) ON CONFLICT (player_id) DO UPDATE SET total_combat_power=EXCLUDED.total_combat_power",
            ("power", (long)power));
        if (levels.Length > 0) await SaveLevelsAsync(connection, transaction, playerId, levels);
        await SaveBoardsAsync(connection, transaction, playerId, personal, false);
        await SaveBoardsAsync(connection, transaction, playerId, global, true);
        await transaction.CommitAsync();
        // SQL 커밋까지 성공해야 dirty를 지운다. 응답을 잃어도 같은 스냅샷을 다시 저장할 수 있다.
        await database.SetRemoveAsync(ProgressRepository.DirtyPlayers, playerId);
    }

    private static Task SaveCurrencyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long playerId, HashEntry[] entries)
    {
        long Value(string field) => entries.Where(entry => entry.Name == field).Select(entry => (long)entry.Value).SingleOrDefault();
        return ExecuteAsync(connection, transaction, playerId, """
            INSERT INTO player_currency (player_id,gold,elleaf,macaron) VALUES (@pid,@gold,@elleaf,@macaron)
            ON CONFLICT (player_id) DO UPDATE SET gold=EXCLUDED.gold,elleaf=EXCLUDED.elleaf,macaron=EXCLUDED.macaron
            """, ("gold", Value("gold")), ("elleaf", Value("elleaf")), ("macaron", Value("macaron")));
    }

    private static Task SaveLevelsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long playerId, HashEntry[] entries) => ExecuteAsync(connection, transaction, playerId, """
            UPDATE character_status SET level=v.level
            FROM (SELECT unnest(@ids) AS id,unnest(@levels) AS level) v
            WHERE player_id=@pid AND character_id=v.id AND status=1
            """, ("ids", entries.Select(entry => (int)entry.Name).ToArray()),
            ("levels", entries.Select(entry => (int)entry.Value).ToArray()));

    private static Task SaveBoardsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long playerId, HashEntry[] entries, bool global)
    {
        if (entries.Length == 0) return Task.CompletedTask;
        var fields = entries.Select(entry => entry.Name.ToString().Split(':').Select(int.Parse).ToArray()).ToArray();
        var table = global ? "player_global_board" : "player_character_board";
        return ExecuteAsync(connection, transaction, playerId, $"""
            INSERT INTO {table} (player_id,character_id,board_no,status)
            SELECT @pid,unnest(@ids),unnest(@numbers),unnest(@statuses)
            ON CONFLICT (player_id,character_id,board_no) DO UPDATE SET status=EXCLUDED.status
            """, ("ids", fields.Select(field => field[0]).ToArray()),
            ("numbers", fields.Select(field => field[1]).ToArray()),
            ("statuses", entries.Select(entry => (short)(long)entry.Value).ToArray()));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long playerId, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("pid", playerId);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }
}
