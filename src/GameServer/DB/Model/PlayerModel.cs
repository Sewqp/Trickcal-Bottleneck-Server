namespace GameServer.DB.Model;

public sealed class PlayerModel
{
    public long PlayerId { get; set; }
    public string PName { get; set; } = "";
    public byte Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
