using GameServer.Grains;
using GameServer.Network;

namespace GameServer.Packet.Handler;

public static class MatchHandler
{
    public static Task HandleAsync(ClientSession session, Memory<byte> packet)
        => OrleansClient.Instance.Factory.GetGrain<IMatchGrain>(0).RequestMatchAsync(session.PlayerId);

    // 클라가 대기를 포기(타임아웃)했을 때 큐에서 빼달라는 신호 — 안 부르면 나중에 접속한 다른
    // 플레이어가 이미 포기한 이 플레이어와 "매칭 성사"로 잘못 묶여버림(죽은 세션과 매칭되는 버그).
    public static Task HandleCancelAsync(ClientSession session, Memory<byte> packet)
        => OrleansClient.Instance.Factory.GetGrain<IMatchGrain>(0).CancelMatchAsync(session.PlayerId);
}
