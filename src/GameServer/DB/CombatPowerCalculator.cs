using System.Text.Json;

namespace GameServer.DB;

// 전투력 환산 — 신규서버_설계계획서_초안.pdf 확정 공식: 0.1×HP + 2×메인공격력 + 물리방어력 + 마법방어력.
// 공식이 스탯에 대해 선형이라, 델타 하나(캐릭터 획득 시 기본스탯/보드 해금/레벨업 증가분)만 넣어도
// 전체 재계산과 같은 증가분이 나옴 — PlayerStatRepository.IncrementCombatPowerAsync(delta)와 그대로 맞물림.
public static class CombatPowerCalculator
{
    public readonly record struct StatVector(long Hp, long MainAtk, long PhysDef, long MagicDef)
    {
        public static StatVector Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new StatVector(
                root.GetProperty("hp").GetInt64(),
                root.GetProperty("main_atk").GetInt64(),
                root.GetProperty("phys_def").GetInt64(),
                root.GetProperty("magic_def").GetInt64());
        }

        public static StatVector operator *(StatVector s, long levels) =>
            new(s.Hp * levels, s.MainAtk * levels, s.PhysDef * levels, s.MagicDef * levels);
    }

    public static long FromStats(StatVector s) =>
        (long)Math.Round(0.1 * s.Hp + 2 * s.MainAtk + s.PhysDef + s.MagicDef, MidpointRounding.AwayFromZero);

    // character_board/global_board.stat_type(0=HP 1=메인공격력 2=물리방어 3=마법방어) 델타 하나를 환산
    public static long FromStatDelta(int statType, long statValue) => statType switch
    {
        0 => (long)Math.Round(0.1 * statValue, MidpointRounding.AwayFromZero),
        1 => 2 * statValue,
        2 => statValue,
        3 => statValue,
        _ => 0,
    };
}
