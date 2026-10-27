using System.Diagnostics;
using Apm.Agent;
using GameServer.Grains;
using GameServer.Metrics;
using GameServer.Network;

namespace GameServer.Packet;

public sealed class PacketDispatcher
{
    public static readonly PacketDispatcher Instance = new();
    private static readonly HashSet<PacketId> NoAuthRequired =
        [PacketId.LoginRequest, PacketId.ReconnectRequest, PacketId.Heartbeat];
    private static readonly HashSet<PacketId> AllowedWhileLocked =
        [PacketId.TheaterExitRequest, PacketId.TheaterTouchRequest, PacketId.Heartbeat, PacketId.ReconnectRequest];
    private readonly Dictionary<PacketId, Func<ClientSession, Memory<byte>, Task>> _handlers = new();
    private PacketDispatcher() { }

    public void Register(PacketId id, Func<ClientSession, Memory<byte>, Task> handler) => _handlers[id] = handler;

    public async Task DispatchAsync(ClientSession session, Memory<byte> packet)
    {
        var id = packet.Length >= PacketHeader.HeaderSize
            ? (PacketId)BitConverter.ToUInt16(packet.Span[sizeof(ushort)..]) : (PacketId)0;
        var name = Enum.IsDefined(id) ? id.ToString() : "UnknownPacket";
        using var request = ApmReporter.Instance.StartRequest(name, session);
        var started = Stopwatch.GetTimestamp();
        var completed = false;
        try
        {
            if (!ValidatePacket(session, packet, id)) return;
            if (await IsBlockedAsync(session, id)) return;
            using (LocalTelemetry.StartSpan($"handler.{name}", "handler"))
                await _handlers[id](session, packet);
            if (id is PacketId.TheaterTouchRequest or PacketId.MatchCancelRequest)
                LocalTelemetry.SetOutcome("success", "accepted_no_response");
            completed = true;
        }
        catch (Exception exception)
        {
            LocalTelemetry.RecordException(exception);
            throw;
        }
        finally
        {
            // Login learns the user identity during handling; attach the pseudonym before export.
            ApmReporter.Instance.RefreshUser(request, session.PlayerId);
            if (id != PacketId.Heartbeat && id != PacketId.TheaterTouchRequest)
                PlayerWatcher.Log(session.PlayerId, $"{id} ({(completed ? "COMPLETED" : "INCOMPLETE")}, {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}ms)");
        }
    }

    private bool ValidatePacket(ClientSession session, Memory<byte> packet, PacketId id)
    {
        string? reason = packet.Length < PacketHeader.HeaderSize ? "malformed_header" :
            !_handlers.ContainsKey(id) ? "unknown_packet" :
            session.PlayerId == 0 && !NoAuthRequired.Contains(id) ? "unauthenticated" : null;
        if (reason is null) return true;
        LocalTelemetry.SetOutcome("rejected", reason);
        return false;
    }

    private static async Task<bool> IsBlockedAsync(ClientSession session, PacketId id)
    {
        if (session.PlayerId == 0 || AllowedWhileLocked.Contains(id)) return false;
        using var wait = LocalTelemetry.StartSpan("session.lock_check", "grain");
        var grain = OrleansClient.Instance.Factory.GetGrain<IPlayerGrain>(session.PlayerId);
        if (!await grain.IsLockedAsync()) return false;
        LocalTelemetry.SetOutcome("rejected", "session_locked");
        return true;
    }
}
