namespace GameServer.DB.Model;

// info/base_stat/stat_growth_table은 JSONB 원본 텍스트 그대로 보관 — 전투력 계산 로직이
// 실제로 필요해질 때 파싱(지금 컬럼 단위로 미리 쪼개지 않음)
public sealed class CharacterInfoModel
{
    public int CharacterId { get; set; }
    public string InfoJson { get; set; } = "";
    public string BaseStatJson { get; set; } = "";
    public string StatGrowthTableJson { get; set; } = "";
}
