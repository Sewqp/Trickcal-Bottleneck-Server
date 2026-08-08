namespace GameServer.DB.Model;

public sealed class PlayerCharacterBoardModel
{
    public long PlayerId { get; set; }
    public int CharacterId { get; set; }
    public int BoardNo { get; set; }
    public byte Status { get; set; } // 0=미해금 1=해금
}
