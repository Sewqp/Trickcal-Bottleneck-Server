using GameServer.Progress;
using static GameServer.DB.CombatPowerCalculator;

namespace GameServer.Checks;

internal static class UnitChecks
{
    public static void Run()
    {
        var random = new Random(4197);
        for (int sample = 0; sample < 200; sample++)
        {
            var state = new ProgressState();
            for (int id = 1; id <= 4; id++)
            {
                state.Characters[id] = new(id, new(random.Next(1, 1500), 120, 60, 40),
                    new(id % 2 == 0 ? 6 : 8, 3, 2, 1));
                state.Levels[id] = 1;
                state.PersonalBoards[$"{id}:1"] = new(id, 1, 100, new(3, 1, 0, 0));
                state.GlobalBoards[$"{id}:1"] = new(id, 1, 100, new(7, 2, 1, 1));
            }
            long incremental = Oracle(state);
            for (int step = 0; step < 100; step++)
            {
                int id = random.Next(1, 5);
                string field = $"{id}:1";
                if (step % 7 == 0 && state.UnlockedGlobal.Add(field))
                {
                    state.UnlockedGlobal.Remove(field);
                    incremental += state.BoardDelta(state.GlobalBoards[field], true);
                    state.UnlockedGlobal.Add(field);
                }
                else if (step % 5 == 0 && state.UnlockedPersonal.Add(field))
                {
                    state.UnlockedPersonal.Remove(field);
                    incremental += state.BoardDelta(state.PersonalBoards[field], false);
                    state.UnlockedPersonal.Add(field);
                }
                else
                {
                    int target = state.Levels[id] + random.Next(1, 8);
                    incremental += state.EnhancementDelta(id, target);
                    state.Levels[id] = target;
                }
                Check.That(incremental == Oracle(state), "mixed progression matches independent total");
                Check.That(state.CalculateTotal() == Oracle(state), "reconciliation matches independent total");
            }
            state.Characters[5] = new(5, new(653, 160, 40, 30), new(6, 4, 1, 1));
            incremental += state.AcquisitionPower([5]);
            state.Levels[5] = 1;
            Check.That(incremental == Oracle(state), "new character inherits unlocked global boards");
        }
        Console.WriteLine("PASS: randomized rounding/global-board/acquisition regression checks");
    }

    // Independent integer formula for nonnegative seed stats, without calling the production calculator.
    private static long Oracle(ProgressState state)
    {
        long total = 0;
        foreach (var (id, level) in state.Levels)
        {
            var definition = state.Characters[id];
            long hp = definition.Base.Hp + definition.Growth.Hp * (level - 1L);
            long attack = definition.Base.MainAtk + definition.Growth.MainAtk * (level - 1L);
            long physical = definition.Base.PhysDef + definition.Growth.PhysDef * (level - 1L);
            long magical = definition.Base.MagicDef + definition.Growth.MagicDef * (level - 1L);
            var boards = state.UnlockedPersonal.Select(key => state.PersonalBoards[key]).Where(b => b.CharacterId == id)
                .Concat(state.UnlockedGlobal.Select(key => state.GlobalBoards[key]));
            foreach (var board in boards)
            {
                hp += board.Stats.Hp; attack += board.Stats.MainAtk;
                physical += board.Stats.PhysDef; magical += board.Stats.MagicDef;
            }
            total += (hp + 5) / 10 + 2 * attack + physical + magical;
        }
        return total;
    }
}
