namespace GameServer.DB.Model;

public sealed class InventoryItemModel
{
    public long InventoryId { get; set; }
    public long ItemInstanceId { get; set; }
    public int ItemDefId { get; set; }
    public string ItemName { get; set; } = "";
    public byte ItemType { get; set; }
    public int ItemCount { get; set; }
}
