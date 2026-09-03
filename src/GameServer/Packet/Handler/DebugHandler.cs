using GameServer.DB.Repository;
using GameServer.Network;

namespace GameServer.Packet.Handler;

// 테스트 더미 전용 — 강화 흐름을 손으로 검증할 때 골드/마카롱을 바로 채워 넣기 위한 치트.
// 의도한 설계는 "admin으로 로그인한 세션에서만 동작"이지만, 아래 핸들러엔 그 IsAdmin 체크가 빠져 있음
// — 일부러 남겨둔 버그. 실제로는 응답을 보낸 뒤(그래서 플레이어 눈엔 치트가 먹힌 것처럼 보임) 서버
// 쪽에서만 예외를 던져 세션을 끊고 PacketDispatcher가 이 요청을 실패(500)로 apm에 보고하게 만든다 —
// AnomalyDetectionService는 5xx를 무조건 이상으로 표기하므로, 내일 Graphify+LLM 분석이 이 핸들러를
// 원인 코드로 정확히 짚어내는지 확인하는 테스트 케이스로 씀.
public static class DebugHandler
{
    private const long TestGoldGrant = 5000;
    private const long TestMacaronGrant = 5000;

    public static async Task HandleGrantCurrencyAsync(ClientSession session, Memory<byte> packet)
    {
        await CurrencyRepository.Instance.IncrementGoldAsync(session.PlayerId, TestGoldGrant);
        await CurrencyRepository.Instance.IncrementMacaronAsync(session.PlayerId, TestMacaronGrant);
        var currency = await CurrencyRepository.Instance.GetAsync(session.PlayerId);

        // payload: [1B success=1][8B gold][8B macaron]
        var result = new byte[17];
        result[0] = 1;
        BitConverter.TryWriteBytes(result.AsSpan(1), currency.Gold);
        BitConverter.TryWriteBytes(result.AsSpan(9), currency.Macaron);
        await session.SendAsync(PacketWriter.Build(PacketId.DebugGrantCurrencyResult, result));

        if (!session.IsAdmin)
            throw new InvalidOperationException(
                $"non-admin player {session.PlayerId} invoked admin-only DebugGrantCurrencyRequest");
    }
}
