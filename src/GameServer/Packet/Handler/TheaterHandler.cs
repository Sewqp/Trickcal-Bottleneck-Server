using GameServer.DB.Repository;
using GameServer.Grains;
using GameServer.Network;

namespace GameServer.Packet.Handler;

// 이 게임은 게임 진행 중 스토리가 나오는 게 아니라 극장에 들어가야만 스토리를 볼 수 있음 —
// 그래서 "극장 안인가(0/1)"만 확실하면 됨. 그레인 락 자체가 곧 그 0/1 플래그(진입=락, 퇴장=언락) —
// 별도 상태 필드를 안 둠. 트릭컬 스토리는 재생 중 스탯/재화 변화가 없는 컨텐츠라, 퇴장 시점 값이
// 입장 시점 스냅샷과 다르면 위변조로 간주하고 스냅샷 값으로 강제 복구함.
//
// 알려진 한계: 하드 타임아웃(EXIT이 영영 안 오는 경우 자동 락 해제) 안전장치는 아직 구현 안 됨 —
// 지금은 그레인 락이 걸린 채로 EXIT을 못 받으면 그 플레이어는 하트비트로도 못 끊김. 다음 개선 대상.
public static class TheaterHandler
{
    public static async Task HandleEnterAsync(ClientSession session, Memory<byte> packet)
    {
        var grain = OrleansClient.Instance.Factory.GetGrain<IPlayerGrain>(session.PlayerId);

        if (!await grain.LockAsync())
        {
            await session.SendAsync(PacketWriter.Build(PacketId.TheaterEnterResult, new byte[] { 0 }));
            return;
        }

        long combatPower = await PlayerStatRepository.Instance.GetCombatPowerAsync(session.PlayerId);
        var currency = await CurrencyRepository.Instance.GetAsync(session.PlayerId);

        await grain.SaveSnapshotAsync(new TheaterSnapshot
        {
            CombatPower = combatPower,
            Gold = currency.Gold,
            Elleaf = currency.Elleaf,
            Macaron = currency.Macaron,
        });

        await session.SendAsync(PacketWriter.Build(PacketId.TheaterEnterResult, new byte[] { 1 }));
    }

    public static async Task HandleExitAsync(ClientSession session, Memory<byte> packet)
    {
        var grain = OrleansClient.Instance.Factory.GetGrain<IPlayerGrain>(session.PlayerId);

        if (!await grain.IsLockedAsync())
        {
            // 극장에 들어간 적 없는데 EXIT을 보냄 — 위장 진입 시도로 보고 거부
            await session.SendAsync(PacketWriter.Build(PacketId.TheaterExitResult, new byte[] { 0 }));
            return;
        }

        var snapshot = await grain.GetSnapshotAsync();
        long combatPower = await PlayerStatRepository.Instance.GetCombatPowerAsync(session.PlayerId);
        var currency = await CurrencyRepository.Instance.GetAsync(session.PlayerId);

        bool tampered = snapshot == null
            || combatPower != snapshot.CombatPower
            || currency.Gold != snapshot.Gold
            || currency.Elleaf != snapshot.Elleaf
            || currency.Macaron != snapshot.Macaron;

        if (tampered && snapshot != null)
        {
            await PlayerStatRepository.Instance.SetCombatPowerAsync(session.PlayerId, snapshot.CombatPower);
            await CurrencyRepository.Instance.SetAllAsync(session.PlayerId, snapshot.Gold, snapshot.Elleaf, snapshot.Macaron);
        }

        await grain.UnlockAsync(); // 처리 완료 신호 — 여기서 락 해제

        var final = snapshot ?? new TheaterSnapshot
        {
            CombatPower = combatPower,
            Gold = currency.Gold,
            Elleaf = currency.Elleaf,
            Macaron = currency.Macaron,
        };

        // payload: [1B tampered][8B combatPower][8B gold][8B elleaf][8B macaron]
        var result = new byte[33];
        result[0] = (byte)(tampered ? 1 : 0);
        BitConverter.TryWriteBytes(result.AsSpan(1), final.CombatPower);
        BitConverter.TryWriteBytes(result.AsSpan(9), final.Gold);
        BitConverter.TryWriteBytes(result.AsSpan(17), final.Elleaf);
        BitConverter.TryWriteBytes(result.AsSpan(25), final.Macaron);
        await session.SendAsync(PacketWriter.Build(PacketId.TheaterExitResult, result));
    }
}
