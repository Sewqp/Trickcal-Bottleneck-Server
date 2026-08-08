using System.Text;
using GameServer.DB;
using GameServer.DB.Repository;
using GameServer.Grains;
using GameServer.Network;

namespace GameServer.Packet.Handler;

public static class ReconnectHandler
{
    private const int TokenLength = 36; // UUID string length

    public static async Task HandleAsync(ClientSession session, Memory<byte> packet)
    {
        if (packet.Length < PacketHeader.HeaderSize + TokenLength) return;

        var token = Encoding.UTF8.GetString(packet.Span.Slice(PacketHeader.HeaderSize, TokenLength));
        var value = await RedisClient.Instance.Db.StringGetDeleteAsync($"reconnect:{token}");

        long playerId = 0;
        bool success = value.HasValue && long.TryParse((string?)value, out playerId);
        if (success)
        {
            await session.AttachPlayerAsync(playerId);

            var guildId = await GuildRepository.Instance.GetGuildIdByPlayerAsync(playerId);
            if (guildId.HasValue)
                await OrleansClient.Instance.Factory.GetGrain<IGuildGrain>(guildId.Value).MemberOnlineAsync(playerId);

            Console.WriteLine($"[Reconnect] Session {session.SessionId} restored PlayerId={playerId}");
        }

        await session.SendAsync(PacketWriter.Build(PacketId.ReconnectResult, [(byte)(success ? 1 : 0)]));
    }
}
