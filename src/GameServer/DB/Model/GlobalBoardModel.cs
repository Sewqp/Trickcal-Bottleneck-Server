namespace GameServer.DB.Model;

// character_id는 효과 대상이 아니라 배치 위치일 뿐 — 효과는 보유 캐릭터 전원에게 적용됨
public sealed class GlobalBoardModel
{
    public int CharacterId { get; set; }
    public int BoardNo { get; set; }
    public int Cost { get; set; }
    public byte StatType { get; set; }
    public int StatValue { get; set; }
}
