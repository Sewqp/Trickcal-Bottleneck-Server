using Orleans;
using GameServer.Packet;

namespace GameServer.Grains;

public sealed class MatchGrain : Grain, IMatchGrain
{
    private readonly List<long> _queue = new();
    private readonly Dictionary<long, PendingRequest> _requests = new();
    private long _nextMatchId;

    public async Task<MatchRequestStatus> RequestMatchAsync(long playerId, Guid sessionId, string? requestTraceParent)
    {
        if (!await GrainFactory.GetGrain<IPlayerGrain>(playerId).IsCurrentSessionAsync(sessionId))
            return MatchRequestStatus.StaleSession;
        if (_requests.TryGetValue(playerId, out var pending) && pending.SessionId == sessionId)
            return MatchRequestStatus.AlreadyQueued;

        // 중복 대기는 최초 원인을 유지하고, 교체된 세션의 새 요청만 원인을 갱신한다.
        _requests[playerId] = new(sessionId, requestTraceParent);
        if (!_queue.Contains(playerId)) _queue.Add(playerId);
        if (_queue.Count < 2) return MatchRequestStatus.Queued;

        long first = _queue[0], second = _queue[1];
        var firstRequest = _requests[first];
        var secondRequest = _requests[second];
        _queue.RemoveRange(0, 2);
        _requests.Remove(first);
        _requests.Remove(second);
        return await NotifyMatchAsync(first, firstRequest, second, secondRequest);
    }

    private async Task<MatchRequestStatus> NotifyMatchAsync(long first, PendingRequest firstRequest,
        long second, PendingRequest secondRequest)
    {
        var payload = new byte[9];
        payload[0] = 1;
        BitConverter.TryWriteBytes(payload.AsSpan(1), ++_nextMatchId);
        var packet = PacketWriter.Build(PacketId.MatchResult, payload);
        var forwarded = await Task.WhenAll(
            GrainFactory.GetGrain<IPlayerGrain>(first).SendMatchResultAsync(firstRequest.SessionId, packet, firstRequest.TraceParent),
            GrainFactory.GetGrain<IPlayerGrain>(second).SendMatchResultAsync(secondRequest.SessionId, packet, secondRequest.TraceParent));

        // OneWay observer 호출 완료는 TCP 쓰기 완료가 아니다. 실제 전송은 별도 MatchResult trace로 관측한다.
        return forwarded.All(value => value) ? MatchRequestStatus.Created : MatchRequestStatus.NotificationUnavailable;
    }

    public Task CancelMatchAsync(long playerId, Guid sessionId)
    {
        if (_requests.TryGetValue(playerId, out var request) && request.SessionId == sessionId)
        {
            _queue.Remove(playerId);
            _requests.Remove(playerId);
        }
        return Task.CompletedTask;
    }

    private sealed record PendingRequest(Guid SessionId, string? TraceParent);
}
