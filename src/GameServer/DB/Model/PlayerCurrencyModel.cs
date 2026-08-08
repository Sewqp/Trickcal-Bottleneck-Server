namespace GameServer.DB.Model;

public sealed class PlayerCurrencyModel
{
    public long PlayerId { get; set; }
    public long Gold { get; set; }    // 무료 재화(보드 해금 등)
    public long Elleaf { get; set; }  // 유료 재화(가챠 전용)
    public long Macaron { get; set; } // 캐릭터 강화 전용 재화
}
