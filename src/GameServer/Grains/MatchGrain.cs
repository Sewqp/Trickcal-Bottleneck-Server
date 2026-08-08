using Orleans;
using GameServer.Packet;

namespace GameServer.Grains;

public sealed class MatchGrain : Grain, IMatchGrain
{
    private readonly List<long> _queue = new();
    private long _nextMatchId;

    public async Task RequestMatchAsync(long playerId)
    {
        if (!_queue.Contains(playerId))
            _queue.Add(playerId);

        if (_queue.Count < 2)
            return;

        long a = _queue[0];
        long b = _queue[1];
        _queue.RemoveRange(0, 2);
        long matchId = ++_nextMatchId;

        // payload: [1B success][8B matchId]
        var payload = new byte[9];
        payload[0] = 1;
        BitConverter.TryWriteBytes(payload.AsSpan(1), matchId);
        var packet = PacketWriter.Build(PacketId.MatchResult, payload);

        await Task.WhenAll(
            GrainFactory.GetGrain<IPlayerGrain>(a).SendMessageAsync(packet),
            GrainFactory.GetGrain<IPlayerGrain>(b).SendMessageAsync(packet));
    }

    public Task CancelMatchAsync(long playerId)
    {
        _queue.Remove(playerId);
        return Task.CompletedTask;
    }
}
