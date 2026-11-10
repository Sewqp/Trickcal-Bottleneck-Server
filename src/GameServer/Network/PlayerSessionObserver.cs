using System.Diagnostics;
using Apm.Agent;
using GameServer.Grains;
using GameServer.Metrics;

namespace GameServer.Network;

public sealed class PlayerSessionObserver : IPlayerSessionObserver
{
    private readonly ClientSession _session;

    public PlayerSessionObserver(ClientSession session) => _session = session;

    public Task DeliverAsync(byte[] packet) => _session.SendAsync(packet);

    public async Task DeliverMatchResultAsync(byte[] packet, string? requestTraceParent)
    {
        // Observer의 수집되지 않은 Orleans 부모를 상속하지 않는다. 원 요청은 별도의 causal link로 남긴다.
        var previous = Activity.Current;
        try
        {
            Activity.Current = null;
            using var notification = ApmReporter.Instance.StartMatchResult(_session, requestTraceParent);
            try { await _session.SendAsync(packet); }
            catch (Exception error)
            {
                LocalTelemetry.RecordException(error);
                throw;
            }
        }
        finally { Activity.Current = previous; }
    }
}
