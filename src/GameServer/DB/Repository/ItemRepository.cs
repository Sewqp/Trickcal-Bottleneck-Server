using Npgsql;
using GameServer.DB.Model;

namespace GameServer.DB.Repository;

public sealed class ItemRepository
{
    public static readonly ItemRepository Instance = new();
    private ItemRepository() { }

    public async Task<long> AcquireItemAsync(long playerId, int itemDefId, int count)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        long itemInstanceId;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO item_instance (item_def_id) VALUES (@defId) RETURNING item_instance_id";
            cmd.Parameters.AddWithValue("@defId", itemDefId);
            itemInstanceId = (long)(await cmd.ExecuteScalarAsync())!;
        }

        long inventoryId;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO inventory (player_id, item_instance_id, item_count)
                VALUES (@pid, @iid, @count)
                RETURNING inventory_id
                """;
            cmd.Parameters.AddWithValue("@pid", playerId);
            cmd.Parameters.AddWithValue("@iid", itemInstanceId);
            cmd.Parameters.AddWithValue("@count", count);
            inventoryId = (long)(await cmd.ExecuteScalarAsync())!;
        }

        await tx.CommitAsync();
        return inventoryId;
    }

    public async Task<List<InventoryItemModel>> GetInventoryAsync(long playerId)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT inv.inventory_id, inv.item_instance_id, def.item_def_id, def.item_name, def.item_type, inv.item_count
            FROM inventory inv
            JOIN item_instance ii ON ii.item_instance_id = inv.item_instance_id
            JOIN item_definition def ON def.item_def_id = ii.item_def_id
            WHERE inv.player_id = @pid AND ii.item_status = 0
            ORDER BY inv.acquired_at
            """;
        cmd.Parameters.AddWithValue("@pid", playerId);

        var result = new List<InventoryItemModel>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new InventoryItemModel
            {
                InventoryId = reader.GetInt64(0),
                ItemInstanceId = reader.GetInt64(1),
                ItemDefId = reader.GetInt32(2),
                ItemName = reader.GetString(3),
                ItemType = (byte)reader.GetInt16(4),
                ItemCount = reader.GetInt32(5),
            });
        }
        return result;
    }

    // count만큼 차감, 0이 되면 빈 인벤토리 행 정리. 보유 수량 부족/타인 소유면 false.
    public async Task<bool> UseItemAsync(long playerId, long inventoryId, int count)
    {
        await using var conn = DbConnectionPool.Instance.GetConnection();
        await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE inventory SET item_count = item_count - @count
                WHERE inventory_id = @id AND player_id = @pid AND item_count >= @count
                """;
            cmd.Parameters.AddWithValue("@count", count);
            cmd.Parameters.AddWithValue("@id", inventoryId);
            cmd.Parameters.AddWithValue("@pid", playerId);
            int affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0) return false;
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM inventory WHERE inventory_id = @id AND item_count = 0";
            cmd.Parameters.AddWithValue("@id", inventoryId);
            await cmd.ExecuteNonQueryAsync();
        }

        return true;
    }
}
