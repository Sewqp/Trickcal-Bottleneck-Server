using GameServer.DB;
using GameServer.DB.Repository;
using GameServer.Grains;
using GameServer.Network;

namespace GameServer.Packet.Handler;

// 1000엘리프로 10연챠, 랜덤 10개 캐릭터(중복 허용, 복원추출) — 중복이면 그대로 보유 유지 + 엘리프 100 보상.
// 위변조 방지를 위해 (1) 소비는 redis 즉시 차감이 기준값 (2) 실제 획득은 서버가 결정해 SQL에 직접 반영 (3)
// 처리 중엔 그레인 락으로 재접속 판정을 멈춤.
public static class GachaHandler
{
    private const int PullCount = 10;
    private const long ElleafCost = 1000;
    private const long DuplicateCompensation = 100;

    public static async Task HandleAsync(ClientSession session, Memory<byte> packet)
    {
        var grain = OrleansClient.Instance.Factory.GetGrain<IPlayerGrain>(session.PlayerId);

        if (!await grain.LockAsync())
        {
            // 이미 처리 중(중복 요청) — 잔액 부족과 같은 실패 코드로 응답
            await session.SendAsync(PacketWriter.Build(PacketId.GachaResult, new byte[] { 0 }));
            return;
        }

        try
        {
            bool spent = await CurrencyRepository.Instance.TrySpendElleafAsync(session.PlayerId, ElleafCost);
            if (!spent)
            {
                await session.SendAsync(PacketWriter.Build(PacketId.GachaResult, new byte[] { 0 }));
                return;
            }

            var pool = await CharacterRepository.Instance.GetAllCharacterIdsAsync();
            var draws = new int[PullCount];
            for (int i = 0; i < PullCount; i++)
                draws[i] = pool[Random.Shared.Next(pool.Count)];

            // 배치 안에서 같은 캐릭터가 중복 추첨돼도 실제 활성화는 정확히 한 번만 — 그래서
            // 중복 제거한 집합만 SQL에 넘김(AcquireCharactersAsync가 이미 보유 중인 건 알아서 걸러줌)
            var distinctDraws = draws.Distinct().ToArray();
            var acquiredIds = new HashSet<int>(
                await CharacterRepository.Instance.AcquireCharactersAsync(session.PlayerId, distinctDraws));

            // 신규 획득 캐릭터는 레벨 1(base_stat 그대로) 기준 전투력을 플레이어 총 전투력에 가산
            foreach (var cid in acquiredIds)
            {
                var info = await CharacterRepository.Instance.GetCharacterInfoAsync(cid);
                if (info is null) continue;
                var baseStat = CombatPowerCalculator.StatVector.Parse(info.BaseStatJson);
                await PlayerStatRepository.Instance.IncrementCombatPowerAsync(
                    session.PlayerId, CombatPowerCalculator.FromStats(baseStat));
            }

            var seen = new HashSet<int>();
            var isNew = new bool[PullCount];
            long compensation = 0;
            for (int i = 0; i < PullCount; i++)
            {
                int cid = draws[i];
                // 이 배치에서 새로 획득됐고(acquiredIds) 아직 이번 순회에서 처음 보는 경우만 "신규" —
                // 같은 캐릭터가 배치 안에서 두 번째로 나오면 첫 등장 때 이미 활성화됐으므로 중복 취급
                if (acquiredIds.Contains(cid) && seen.Add(cid))
                    isNew[i] = true;
                else
                    compensation += DuplicateCompensation;
            }

            if (compensation > 0)
                await CurrencyRepository.Instance.IncrementElleafAsync(session.PlayerId, compensation);

            // payload: [1B success=1][2B compensationElleaf][10 * {4B characterId, 1B isNew}]
            var result = new byte[1 + 2 + PullCount * 5];
            result[0] = 1;
            BitConverter.TryWriteBytes(result.AsSpan(1), (ushort)compensation);
            int offset = 3;
            for (int i = 0; i < PullCount; i++)
            {
                BitConverter.TryWriteBytes(result.AsSpan(offset), draws[i]); offset += 4;
                result[offset] = (byte)(isNew[i] ? 1 : 0); offset += 1;
            }

            await session.SendAsync(PacketWriter.Build(PacketId.GachaResult, result));
        }
        finally
        {
            // 처리 완료 신호 — 여기서 락 해제(하트비트 타임아웃이 아니라 명시적 완료로 해제)
            await grain.UnlockAsync();
        }
    }
}
