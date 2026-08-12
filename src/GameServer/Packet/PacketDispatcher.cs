using GameServer.Grains;
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

    // 그레인 락(가챠 처리 중/극장 관람 중)이 걸린 동안에도 허용하는 패킷 — 그 외는 전부 드롭.
    // 극장은 "관람 아니면 퇴장" 말고 다른 루트가 없다는 게임 규칙을 서버가 직접 강제하기 위함.
    // 화이트리스트 방식이라 새 핸들러를 추가해도 기본값이 "락 중엔 차단"이라 깜빡할 위험이 없음
    // (BoardHandler/CharacterHandler에 개별로 락 체크를 넣었던 이전 방식은 이 게이트로 대체됨).
    private static readonly HashSet<PacketId> AllowedWhileLocked = new()
    {
        PacketId.TheaterExitRequest,
        PacketId.TheaterTouchRequest,
        PacketId.Heartbeat,
        PacketId.ReconnectRequest,
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

        if (session.PlayerId != 0 && !AllowedWhileLocked.Contains(id))
        {
            var grain = OrleansClient.Instance.Factory.GetGrain<IPlayerGrain>(session.PlayerId);
            if (await grain.IsLockedAsync()) return;
        }

        await handler(session, packet);
    }
}
