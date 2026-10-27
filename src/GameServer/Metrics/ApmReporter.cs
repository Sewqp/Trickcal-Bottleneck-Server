using System.Diagnostics;
using System.Text;
using Apm.Agent;
using GameServer.Network;
using GameServer.Packet;

namespace GameServer.Metrics;

public sealed class ApmReporter : IAsyncDisposable
{
    public static readonly ApmReporter Instance = new();
    private LocalTelemetry? _telemetry;
    private ApmReporter() { }

    public void Init(string collectorBaseUrl, string serverName, CancellationToken ct)
    {
        if (_telemetry is not null) throw new InvalidOperationException("APM is already initialized.");
        var key = Environment.GetEnvironmentVariable("APM_USER_HASH_KEY");
        _telemetry = new LocalTelemetry(new AgentOptions
        {
            Endpoint = new Uri(collectorBaseUrl), ServiceName = serverName,
            RunId = Environment.GetEnvironmentVariable("APM_RUN_ID") ?? Guid.NewGuid().ToString("N"),
            CodeVersion = Environment.GetEnvironmentVariable("APM_CODE_VERSION") ?? "working-tree-unversioned",
            UserHashKey = key is null ? null : Encoding.UTF8.GetBytes(key),
            DetailSampleRate = ReadNumber("APM_DETAIL_SAMPLE_RATE", 0.01),
            SlowTraceMs = ReadNumber("APM_SLOW_TRACE_MS", 100),
            ActiveSessions = () => SessionManager.Instance.Count
        });
    }

    public Activity? StartRequest(string name, ClientSession session) =>
        _telemetry?.StartRequest(name, session.SessionId, session.PlayerId);

    private static double ReadNumber(string key, double fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return value is null ? fallback : double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public void RefreshUser(Activity? activity, long playerId) =>
        activity?.SetTag("apm.user", _telemetry?.UserKey(playerId));

    // Only documented response layouts are interpreted. Unrecognized layouts stay unknown.
    public static void RecordResponse(byte[] data)
    {
        if (data.Length < PacketHeader.HeaderSize) return;
        var id = (PacketId)BitConverter.ToUInt16(data.AsSpan(sizeof(ushort)));
        if (id is PacketId.Heartbeat or PacketId.InventoryResult or PacketId.CharacterListResult or
            PacketId.CharacterEnhanceEnterResult or PacketId.BoardListResult or PacketId.MatchResult)
        {
            LocalTelemetry.SetOutcome("success", id.ToString());
            return;
        }
        if (data.Length <= PacketHeader.HeaderSize) return;
        var flag = data[PacketHeader.HeaderSize];
        bool? success = id switch
        {
            PacketId.LoginResult or PacketId.ReconnectResult or PacketId.ItemAcquireResult or PacketId.ItemUseResult or
            PacketId.GachaResult or PacketId.TheaterEnterResult or PacketId.CharacterEnhanceExitResult or
            PacketId.BoardUnlockResult or PacketId.GuildCreateResult or PacketId.GuildJoinResult or
            PacketId.GuildLeaveResult or PacketId.GuildInfoResult or PacketId.DebugGrantCurrencyResult => flag == 1,
            PacketId.TheaterExitResult when data.Length > PacketHeader.HeaderSize + 1 => flag == 0,
            PacketId.TheaterExitResult => false,
            _ => null
        };
        if (success.HasValue) LocalTelemetry.SetOutcome(success.Value ? "success" : "rejected", id.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        if (_telemetry is not null) await _telemetry.DisposeAsync();
        _telemetry = null;
    }
}
