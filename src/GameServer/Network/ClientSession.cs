using System.Net.Sockets;
using GameServer.DB;
using GameServer.DB.Repository;
using GameServer.Grains;
using GameServer.Packet;

namespace GameServer.Network;

public sealed class ClientSession
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly PacketBuffer _recvBuffer = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationToken _ct;
    private IPlayerSessionObserver? _observerRef;

    public Guid SessionId { get; } = Guid.NewGuid();
    public long PlayerId { get; set; }
    public DateTime LastReceivedAt { get; private set; } = DateTime.UtcNow;

    public void UpdateLastReceived() => LastReceivedAt = DateTime.UtcNow;

    public ClientSession(TcpClient client, CancellationToken ct)
    {
        _client = client;
        _stream = client.GetStream();
        _ct = ct;
    }

    public async Task AttachPlayerAsync(long playerId)
    {
        PlayerId = playerId;
        SessionManager.Instance.RegisterPlayerId(playerId, SessionId);
        _observerRef ??= OrleansClient.Instance.Factory.CreateObjectReference<IPlayerSessionObserver>(new PlayerSessionObserver(this));
        await OrleansClient.Instance.Factory.GetGrain<IPlayerGrain>(playerId).SubscribeAsync(_observerRef);
    }

    public async Task StartAsync()
    {
        try
        {
            await RecvLoopAsync();
        }
        finally
        {
            await DisconnectAsync();
        }
    }

    public async Task SendAsync(byte[] data)
    {
        await _sendLock.WaitAsync(_ct);
        try
        {
            await _stream.WriteAsync(data, _ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        if (PlayerId != 0)
        {
            // redis→PG flush 트리거 ①~③(하트비트 타임아웃/강제종료/정상종료)은 전부 이 경로를 거침 —
            // "세션 1개 flush" 함수 하나로 통일(design-plan-draft.md 5-1절)
            await SessionFlush.FlushOneAsync(PlayerId);

            var guildId = await GuildRepository.Instance.GetGuildIdByPlayerAsync(PlayerId);
            if (guildId.HasValue)
                await OrleansClient.Instance.Factory.GetGrain<IGuildGrain>(guildId.Value).MemberOfflineAsync(PlayerId);

            var grain = OrleansClient.Instance.Factory.GetGrain<IPlayerGrain>(PlayerId);
            await grain.OnDisconnectAsync();
            // 정상 종료/비정상 종료(크래시 등 TCP 레벨 끊김) 둘 다 이 경로 하나로 처리됨 —
            // 가챠/극장 처리 중 끊기면 그레인 락이 영영 안 풀리는 걸 막기 위해 여기서 항상 해제.
            // UnlockAsync는 멱등이라 애초에 안 걸려있었어도 안전.
            await grain.UnlockAsync();
        }

        SessionManager.Instance.UnregisterPlayerId(PlayerId);
        SessionManager.Instance.Remove(SessionId);
        _stream.Close();
        _client.Close();
    }

    private async Task RecvLoopAsync()
    {
        var buffer = new byte[PacketBuffer.MaxPacketSize];

        while (!_ct.IsCancellationRequested)
        {
            int read = await _stream.ReadAsync(buffer, _ct);
            if (read == 0) break;

            UpdateLastReceived();
            if (!_recvBuffer.Write(buffer.AsSpan(0, read))) break;

            Memory<byte>? packet;
            while ((packet = _recvBuffer.TryAssemble()) != null)
                await PacketDispatcher.Instance.DispatchAsync(this, packet.Value);
        }
    }
}
