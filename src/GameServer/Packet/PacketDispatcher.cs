using GameServer.Network;

namespace GameServer.Packet;

public sealed class PacketDispatcher
{
    public static readonly PacketDispatcher Instance = new();

    // 로그인 전(PlayerId == 0) 세션도 처리를 허용하는 패킷 — 그 외는 미인증 세션이면 무시
    private static readonly HashSet<PacketId> NoAuthRequired = new()
    {
        PacketId.LoginRequest,
        PacketId.ReconnectRequest,
        PacketId.Heartbeat,
    };

    private readonly Dictionary<PacketId, Func<ClientSession, Memory<byte>, Task>> _handlers = new();

    private PacketDispatcher() { }

    public void Register(PacketId id, Func<ClientSession, Memory<byte>, Task> handler)
        => _handlers[id] = handler;

    public async Task DispatchAsync(ClientSession session, Memory<byte> packet)
    {
        if (packet.Length < PacketHeader.HeaderSize) return;

        var id = (PacketId)BitConverter.ToUInt16(packet.Span[sizeof(ushort)..]);
        if (!_handlers.TryGetValue(id, out var handler)) return;
        if (session.PlayerId == 0 && !NoAuthRequired.Contains(id)) return;

        await handler(session, packet);
    }
}
