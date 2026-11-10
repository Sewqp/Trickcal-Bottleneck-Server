using Orleans;
using GameServer.Packet;

namespace GameServer.Grains;

public sealed class GuildGrain : Grain, IGuildGrain
{
    private readonly Dictionary<long, Guid> _onlineMembers = new();

    public async Task MemberOnlineAsync(long playerId, Guid sessionId)
    {
        if (!await GrainFactory.GetGrain<IPlayerGrain>(playerId).IsCurrentSessionAsync(sessionId)) return;
        _onlineMembers[playerId] = sessionId;
        await BroadcastStatusAsync(playerId, online: true);
    }

    public async Task MemberOfflineAsync(long playerId, Guid sessionId)
    {
        if (!_onlineMembers.TryGetValue(playerId, out var owner) || owner != sessionId) return;
        _onlineMembers.Remove(playerId);
        await BroadcastStatusAsync(playerId, online: false);
    }

    public Task<List<long>> GetOnlineMembersAsync() => Task.FromResult(_onlineMembers.Keys.ToList());

    private async Task BroadcastStatusAsync(long playerId, bool online)
    {
        // payload: [8B playerId][1B online]
        var payload = new byte[9];
        BitConverter.TryWriteBytes(payload.AsSpan(0), playerId);
        payload[8] = (byte)(online ? 1 : 0);
        var packet = PacketWriter.Build(PacketId.GuildMemberStatusNotify, payload);

        await Task.WhenAll(_onlineMembers.Keys
            .Where(id => id != playerId)
            .Select(id => GrainFactory.GetGrain<IPlayerGrain>(id).SendMessageAsync(packet)));
    }
}
