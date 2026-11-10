using System.Text.Json;

namespace GameServer.DB;

// 전투력 환산 — 신규서버_설계계획서_초안.pdf 확정 공식: 0.1×HP + 2×메인공격력 + 물리방어력 + 마법방어력.
// 반올림 이후에는 선형성이 깨진다. 진행 변경은 캐릭터별 반올림된 전후 값의 차이를 더한다.
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

        public static StatVector operator +(StatVector left, StatVector right) =>
            new(checked(left.Hp + right.Hp), checked(left.MainAtk + right.MainAtk),
                checked(left.PhysDef + right.PhysDef), checked(left.MagicDef + right.MagicDef));

        public static StatVector FromDelta(int type, long value) => type switch
        {
            0 => new(value, 0, 0, 0), 1 => new(0, value, 0, 0),
            2 => new(0, 0, value, 0), 3 => new(0, 0, 0, value),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

        public static StatVector operator *(StatVector s, long levels) =>
            new(checked(s.Hp * levels), checked(s.MainAtk * levels), checked(s.PhysDef * levels), checked(s.MagicDef * levels));
    }

    public static long FromStats(StatVector s) =>
        checked((long)Math.Round(s.Hp / 10m + 2m * s.MainAtk + s.PhysDef + s.MagicDef, MidpointRounding.AwayFromZero));

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
