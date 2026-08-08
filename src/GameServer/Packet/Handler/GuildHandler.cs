using System.Text;
using GameServer.DB.Repository;
using GameServer.Grains;
using GameServer.Network;

namespace GameServer.Packet.Handler;

public static class GuildHandler
{
    public static async Task HandleCreateAsync(ClientSession session, Memory<byte> packet)
    {
        if (packet.Length < PacketHeader.HeaderSize + 2) return;
        var payload = packet.Span[PacketHeader.HeaderSize..];
        ushort nameLen = BitConverter.ToUInt16(payload);
        if (payload.Length < 2 + nameLen) return;
        var name = Encoding.UTF8.GetString(payload.Slice(2, nameLen));

        var guildId = await GuildRepository.Instance.CreateAsync(name, session.PlayerId);

        // payload: [1B success][8B guildId]
        var result = new byte[9];
        result[0] = (byte)(guildId.HasValue ? 1 : 0);
        if (guildId.HasValue) BitConverter.TryWriteBytes(result.AsSpan(1), guildId.Value);
        await session.SendAsync(PacketWriter.Build(PacketId.GuildCreateResult, result));

        if (guildId.HasValue)
            await OrleansClient.Instance.Factory.GetGrain<IGuildGrain>(guildId.Value).MemberOnlineAsync(session.PlayerId);
    }

    public static async Task HandleJoinAsync(ClientSession session, Memory<byte> packet)
    {
        if (packet.Length < PacketHeader.HeaderSize + 8) return;
        long guildId = BitConverter.ToInt64(packet.Span[PacketHeader.HeaderSize..]);

        bool success = await GuildRepository.Instance.JoinAsync(guildId, session.PlayerId);
        await session.SendAsync(PacketWriter.Build(PacketId.GuildJoinResult, new byte[] { (byte)(success ? 1 : 0) }));

        if (success)
            await OrleansClient.Instance.Factory.GetGrain<IGuildGrain>(guildId).MemberOnlineAsync(session.PlayerId);
    }

    public static async Task HandleLeaveAsync(ClientSession session, Memory<byte> packet)
    {
        if (packet.Length < PacketHeader.HeaderSize + 8) return;
        long guildId = BitConverter.ToInt64(packet.Span[PacketHeader.HeaderSize..]);

        bool success = await GuildRepository.Instance.LeaveAsync(guildId, session.PlayerId);
        await session.SendAsync(PacketWriter.Build(PacketId.GuildLeaveResult, new byte[] { (byte)(success ? 1 : 0) }));

        if (success)
            await OrleansClient.Instance.Factory.GetGrain<IGuildGrain>(guildId).MemberOfflineAsync(session.PlayerId);
    }

    public static async Task HandleInfoRequestAsync(ClientSession session, Memory<byte> packet)
    {
        if (packet.Length < PacketHeader.HeaderSize + 8) return;
        long guildId = BitConverter.ToInt64(packet.Span[PacketHeader.HeaderSize..]);

        var guild = await GuildRepository.Instance.GetByIdAsync(guildId);
        if (guild == null)
        {
            await session.SendAsync(PacketWriter.Build(PacketId.GuildInfoResult, new byte[] { 0 }));
            return;
        }

        var members = await GuildRepository.Instance.GetMembersAsync(guildId);
        var nameBytes = Encoding.UTF8.GetBytes(guild.GuildName);

        // payload: [1B success=1][2B nameLen][nameBytes][2B memberCount] + N*{8B playerId, 1B role}
        var result = new byte[1 + 2 + nameBytes.Length + 2 + members.Count * 9];
        result[0] = 1;
        int offset = 1;
        BitConverter.TryWriteBytes(result.AsSpan(offset), (ushort)nameBytes.Length); offset += 2;
        nameBytes.CopyTo(result.AsSpan(offset)); offset += nameBytes.Length;
        BitConverter.TryWriteBytes(result.AsSpan(offset), (ushort)members.Count); offset += 2;
        foreach (var m in members)
        {
            BitConverter.TryWriteBytes(result.AsSpan(offset), m.PlayerId); offset += 8;
            result[offset] = m.Role; offset += 1;
        }

        await session.SendAsync(PacketWriter.Build(PacketId.GuildInfoResult, result));
    }
}
