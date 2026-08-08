namespace GameServer.DB.Model;

public sealed class CharacterStatusModel
{
    public long PlayerId { get; set; }
    public int CharacterId { get; set; }
    public byte Status { get; set; } // 0=미보유 1=보유
    public int Level { get; set; }
    public DateTime? AcquiredAt { get; set; }
}
