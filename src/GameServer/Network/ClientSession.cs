using Apm.Agent;
using GameServer.Metrics;
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
    private readonly object _disconnectGate = new();
    private Task? _disconnectTask;
    private readonly CancellationTokenSource _lifetime;

    public Guid SessionId { get; } = Guid.NewGuid();
    public long PlayerId { get; set; }
    // 로그인 기믹(LoginHandler) — 로그인 이름이 "admin"이면 세션에 이 플래그가 붙음. 실제 인증/권한
    // 체계가 아니라, DebugHandler처럼 "어드민 전용이어야 하는데 체크가 빠진" 버그를 재현하기 위한 표식.
    public bool IsAdmin { get; set; }
    public DateTime LastReceivedAt { get; private set; } = DateTime.UtcNow;

    public void UpdateLastReceived() => LastReceivedAt = DateTime.UtcNow;

    public ClientSession(TcpClient client, CancellationToken ct)
    {
        _client = client;
        _stream = client.GetStream();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ct = _lifetime.Token;
    }

    public async Task AttachPlayerAsync(long playerId)
    {
        lock (_disconnectGate)
        {
            if (_disconnectTask is not null || PlayerId != 0) throw new IOException("Session is closed or already authenticated.");
            PlayerId = playerId;
        }
        _observerRef = OrleansClient.Instance.Factory.CreateObjectReference<IPlayerSessionObserver>(new PlayerSessionObserver(this));
        var grain = OrleansClient.Instance.Factory.GetGrain<IPlayerGrain>(playerId);
        await grain.SubscribeAsync(SessionId, _observerRef);
        lock (_disconnectGate)
        {
            if (_disconnectTask is null)
            {
                SessionManager.Instance.RegisterPlayerId(playerId, SessionId);
                return;
            }
        }
        await grain.OnDisconnectAsync(SessionId);
        throw new IOException("Session closed during login.");
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
        using var send = LocalTelemetry.StartSpan("response.send", "network");
        using (LocalTelemetry.StartSpan("response.send_lock", "wait"))
            await _sendLock.WaitAsync(_ct);
        try
        {
            await _stream.WriteAsync(data, _ct);
            ApmReporter.RecordResponse(data);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task DisconnectAsync()
    {
        lock (_disconnectGate) return _disconnectTask ??= DisconnectCoreAsync();
    }

    private async Task DisconnectCoreAsync()
    {
        // 소켓/플레이어 인덱스는 즉시 정리한다. 세션 목록에는 cleanup 완료까지 남겨 shutdown이 기다리게 한다.
        try
        {
            _lifetime.Cancel();
            _stream.Close();
            _client.Close();
            SessionManager.Instance.UnregisterPlayerId(PlayerId, SessionId);
            if (PlayerId == 0) return;
            var factory = OrleansClient.Instance.Factory;
            await CleanupAsync(() => factory.GetGrain<IPlayerGrain>(PlayerId).OnDisconnectAsync(SessionId));
            await CleanupAsync(async () =>
            {
                var guildId = await GuildRepository.Instance.GetGuildIdByPlayerAsync(PlayerId);
                if (guildId.HasValue)
                    await factory.GetGrain<IGuildGrain>(guildId.Value).MemberOfflineAsync(PlayerId, SessionId);
            });
            await CleanupAsync(() => factory.GetGrain<IMatchGrain>(0).CancelMatchAsync(PlayerId, SessionId));
            if (_observerRef is not null) factory.DeleteObjectReference<IPlayerSessionObserver>(_observerRef);
        }
        finally
        {
            SessionManager.Instance.Remove(SessionId);
        }
    }

    private async Task CleanupAsync(Func<Task> cleanup)
    {
        try { await cleanup(); }
        catch (Exception error)
        {
            // 진행 변경은 Redis dirty 집합에 남아 ProgressFlushWorker가 다시 저장한다.
            Console.Error.WriteLine($"[Disconnect] cleanup failed for {PlayerId}/{SessionId}: {error.Message}");
        }
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
