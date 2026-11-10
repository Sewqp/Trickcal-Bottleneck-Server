using GameServer.DB;
using static GameServer.DB.CombatPowerCalculator;

namespace GameServer.Progress;

// 플레이어 Grain 한 개가 변경을 직렬화한다. Redis 쓰기가 실패하면 이 파생 상태는 버리고 다시 읽는다.
public sealed class ProgressState
{
    public Dictionary<int, CharacterDefinition> Characters { get; } = new();
    public Dictionary<int, int> Levels { get; } = new();
    public Dictionary<string, BoardDefinition> PersonalBoards { get; } = new();
    public Dictionary<string, BoardDefinition> GlobalBoards { get; } = new();
    public HashSet<string> UnlockedPersonal { get; } = new();
    public HashSet<string> UnlockedGlobal { get; } = new();

    public static string BoardField(int characterId, int boardNo) => $"{characterId}:{boardNo}";

    public StatVector GlobalStats => Sum(UnlockedGlobal.Select(key => GlobalBoards[key].Stats));

    public StatVector CharacterStats(int characterId, StatVector global)
    {
        var definition = Characters[characterId];
        var personal = Sum(UnlockedPersonal.Select(key => PersonalBoards[key])
            .Where(board => board.CharacterId == characterId).Select(board => board.Stats));
        return definition.Base + definition.Growth * (Levels[characterId] - 1L) + personal + global;
    }

    // 전체 검산은 최초 로드/복구 때만 사용한다. 강화와 개인 보드는 해당 캐릭터의 전후 차이만 계산한다.
    public long CalculateTotal()
    {
        var global = GlobalStats;
        return Levels.Keys.Sum(id => FromStats(CharacterStats(id, global)));
    }

    public long EnhancementDelta(int characterId, int targetLevel)
    {
        var before = CharacterStats(characterId, GlobalStats);
        var growth = Characters[characterId].Growth * (targetLevel - (long)Levels[characterId]);
        return checked(FromStats(before + growth) - FromStats(before));
    }

    public long BoardDelta(BoardDefinition board, bool isGlobal)
    {
        var global = GlobalStats;
        if (!isGlobal)
        {
            var before = CharacterStats(board.CharacterId, global);
            return checked(FromStats(before + board.Stats) - FromStats(before));
        }
        // 각 캐릭터를 반올림한 뒤 합산하므로, 전체 보드는 단순히 delta * 인원으로 계산하지 않는다.
        return Levels.Keys.Sum(id =>
        {
            var before = CharacterStats(id, global);
            return checked(FromStats(before + board.Stats) - FromStats(before));
        });
    }

    public long AcquisitionPower(IEnumerable<int> newIds)
    {
        var global = GlobalStats;
        return newIds.Sum(id => FromStats(Characters[id].Base + global));
    }

    private static StatVector Sum(IEnumerable<StatVector> vectors) =>
        vectors.Aggregate(new StatVector(), (sum, value) => sum + value);
}

public sealed record CharacterDefinition(int Id, CombatPowerCalculator.StatVector Base,
    CombatPowerCalculator.StatVector Growth);
public sealed record BoardDefinition(int CharacterId, int Number, int Cost,
    CombatPowerCalculator.StatVector Stats);
