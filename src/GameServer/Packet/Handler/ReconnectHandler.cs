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
        if (packet.Length < PacketHeader.HeaderSize + TokenLength) { Apm.Agent.LocalTelemetry.SetOutcome("rejected", "malformed_payload"); return; }

        var token = Encoding.UTF8.GetString(packet.Span.Slice(PacketHeader.HeaderSize, TokenLength));
        var consumed = await RedisClient.Instance.Db.ScriptEvaluateAsync("""
            local value = redis.call('GET', KEYS[1])
            if value then redis.call('DEL', KEYS[1]) end
            return value
            """, [(StackExchange.Redis.RedisKey)$"reconnect:{token}"]);
        var value = (string?)consumed;

        long playerId = 0;
        bool success = value is not null && long.TryParse(value, out playerId);
        if (success)
        {
            await session.AttachPlayerAsync(playerId);

            var guildId = await GuildRepository.Instance.GetGuildIdByPlayerAsync(playerId);
            if (guildId.HasValue)
                await OrleansClient.Instance.Factory.GetGrain<IGuildGrain>(guildId.Value).MemberOnlineAsync(playerId, session.SessionId);

            Console.WriteLine($"[Reconnect] Session {session.SessionId} restored PlayerId={playerId}");
        }

        var response = new byte[success ? 37 : 1];
        response[0] = success ? (byte)1 : (byte)0;
        if (success)
        {
            var replacement = Guid.NewGuid().ToString();
            await RedisClient.Instance.Db.StringSetAsync($"reconnect:{replacement}", playerId, TimeSpan.FromSeconds(300));
            Encoding.UTF8.GetBytes(replacement).CopyTo(response, 1);
        }
        await session.SendAsync(PacketWriter.Build(PacketId.ReconnectResult, response));
    }
}
