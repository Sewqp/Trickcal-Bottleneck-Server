namespace GameServer.DB.Model;

public sealed class CharacterBoardModel
{
    public int CharacterId { get; set; }
    public int BoardNo { get; set; }
    public int Cost { get; set; }
    public byte StatType { get; set; } // 0=HP 1=메인 공격력 2=물리 방어력 3=마법 방어력
    public int StatValue { get; set; }
}
