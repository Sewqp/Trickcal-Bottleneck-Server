using System.Net;
using System.Net.Sockets;
using GameServer.Logging;

namespace GameServer.Network;

public sealed class TcpServer
{
    private const int ConnectionLogInterval = 1000;

    private readonly TcpListener _listener;
    private readonly CancellationToken _ct;
    private readonly int _backlog;
    private readonly int _acceptDelayMs;

    // acceptDelayMs: 데모/시연 전용 — 실제 로직이 아니라 인위적으로 accept 루프를 느리게 만들어서,
    // loopback처럼 accept 자체가 순식간에 끝나는 환경에서도 낮은 backlog가 실제로 접속 실패를 유발하는
    // 걸 보여주기 위함(accept 루프가 이 지연만큼 다음 Accept를 늦게 부르니, backlog 큐가 그 사이에
    // 밀려서 쌓임 — 실제 운영 코드엔 이런 지연이 없어야 함).
    public TcpServer(int port, CancellationToken cancellationToken = default, int backlog = 512, int acceptDelayMs = 0)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _ct = cancellationToken;
        _backlog = backlog;
        _acceptDelayMs = acceptDelayMs;
    }

    public async Task StartAsync()
    {
        _listener.Start(_backlog);
        Console.WriteLine($"[TcpServer] Listening on port {((IPEndPoint)_listener.LocalEndpoint).Port} " +
            $"(backlog={_backlog}, acceptDelayMs={_acceptDelayMs})");
        await AcceptLoopAsync();
    }

    public Task StopAsync()
    {
        _listener.Stop();
        Console.WriteLine("[TcpServer] Stopped.");
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_ct);
                var session = new ClientSession(client, _ct);
                SessionManager.Instance.Add(session);

                int total = SessionManager.Instance.Count;
                if (total % ConnectionLogInterval == 0)
                    Console.WriteLine($"[TcpServer] Sessions connected: {total}");

                _ = session.StartAsync().ContinueWith(
                    t => AsyncLogger.Instance.LogError($"Unhandled session error: {session.SessionId}", t.Exception),
                    TaskContinuationOptions.OnlyOnFaulted);

                if (_acceptDelayMs > 0)
                    await Task.Delay(_acceptDelayMs, _ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AsyncLogger.Instance.LogError("Accept error", ex);
            }
        }
    }
}
