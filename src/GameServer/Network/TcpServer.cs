using System.Net;
using System.Net.Sockets;
using GameServer.Logging;

namespace GameServer.Network;

public sealed class TcpServer
{
    private const int ConnectionLogInterval = 1000;

    private readonly TcpListener _listener;
    private readonly CancellationToken _ct;

    public TcpServer(int port, CancellationToken cancellationToken = default)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _ct = cancellationToken;
    }

    public async Task StartAsync()
    {
        _listener.Start(512);
        Console.WriteLine($"[TcpServer] Listening on port {((IPEndPoint)_listener.LocalEndpoint).Port}");
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
