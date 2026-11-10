using System.Diagnostics;
using Apm.Agent;
using GameServer.Grains;
using GameServer.Network;

namespace GameServer.Packet.Handler;

public static class MatchHandler
{
    public static async Task HandleAsync(ClientSession session, Memory<byte> packet)
    {
        var status = await OrleansClient.Instance.Factory.GetGrain<IMatchGrain>(0)
            .RequestMatchAsync(session.PlayerId, session.SessionId, Activity.Current?.Id);
        var (outcome, code) = status switch
        {
            MatchRequestStatus.Queued => ("success", "match_queued"),
            MatchRequestStatus.AlreadyQueued => ("success", "match_already_queued"),
            MatchRequestStatus.Created => ("success", "match_created"),
            MatchRequestStatus.StaleSession => ("rejected", "match_stale_session"),
            MatchRequestStatus.NotificationUnavailable => ("rejected", "match_notification_unavailable"),
            _ => throw new InvalidOperationException($"Unexpected match request status: {status}")
        };
        LocalTelemetry.SetOutcome(outcome, code);
    }

    // 취소된 요청을 뒤늦게 다른 플레이어와 매칭하지 않도록 세션 소유권을 확인한다.
    public static Task HandleCancelAsync(ClientSession session, Memory<byte> packet)
        => OrleansClient.Instance.Factory.GetGrain<IMatchGrain>(0).CancelMatchAsync(session.PlayerId, session.SessionId);
}
