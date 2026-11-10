using Orleans;
using GameServer.DB;
using GameServer.DB.Repository;
using GameServer.Packet;
using GameServer.Progress;

namespace GameServer.Grains;

// non-reentrant Grain 한 호출 안에서 검증부터 저장까지 마쳐 Handler 사이의 경합을 막는다.
public sealed class PlayerGrain : Grain, IPlayerGrain
{
    private IPlayerSessionObserver? _observer;
    private Guid? _sessionId;
    private DateTime? _lastTouchAt;
    private TheaterSnapshot? _snapshot;
    private ProgressState? _progress;
    private long PlayerId => this.GetPrimaryKeyLong();

    public Task SendMessageAsync(byte[] packet) => _observer?.DeliverAsync(packet) ?? Task.CompletedTask;
    public async Task<bool> SendMatchResultAsync(Guid sessionId, byte[] packet, string? requestTraceParent)
    {
        if (_sessionId != sessionId || _observer is null) return false;
        await _observer.DeliverMatchResultAsync(packet, requestTraceParent);
        return true;
    }

    public Task<bool> IsCurrentSessionAsync(Guid sessionId) => Task.FromResult(_sessionId == sessionId);
    public Task<bool> IsLockedAsync() => Task.FromResult(_snapshot is not null);
    public Task<TimeSpan?> GetLockIdleDurationAsync() =>
        Task.FromResult(_lastTouchAt is { } time ? (TimeSpan?)(DateTime.UtcNow - time) : null);

    public async Task SubscribeAsync(Guid sessionId, IPlayerSessionObserver observer)
    {
        await CurrencyRepository.Instance.GetAsync(PlayerId);
        await PlayerStatRepository.Instance.GetCombatPowerAsync(PlayerId);
        if (await ProgressRepository.RecoverGachaAsync(PlayerId)) _progress = null;
        if (_sessionId != sessionId) ClearTheater();
        _sessionId = sessionId;
        _observer = observer;
    }

    public async Task OnDisconnectAsync(Guid sessionId)
    {
        if (_sessionId != sessionId) return;
        _sessionId = null;
        _observer = null;
        ClearTheater();
        await FlushAsync();
    }

    public async Task FlushAsync()
    {
        if (await ProgressRepository.RecoverGachaAsync(PlayerId)) _progress = null;
        await SessionFlush.FlushOneAsync(PlayerId);
    }

    public async Task<byte[]> ExecuteProgressAsync(Guid sessionId, ushort packetId, byte[] payload)
    {
        if (_sessionId != sessionId) return [];
        var id = (PacketId)packetId;
        if (_snapshot is not null && id is not (PacketId.TheaterExitRequest or PacketId.TheaterTouchRequest)) return [];
        try
        {
            if (await ProgressRepository.RecoverGachaAsync(PlayerId)) _progress = null;
            if (id == PacketId.TheaterTouchRequest)
            {
                if (_snapshot is not null) _lastTouchAt = DateTime.UtcNow;
                return [];
            }
            if (id == PacketId.TheaterExitRequest) return await ExitTheaterAsync();
            _progress ??= await ProgressRepository.LoadAsync(PlayerId);
            var commands = new ProgressCommands(PlayerId, _progress);
            return id switch
            {
                PacketId.BoardUnlockRequest => await commands.UnlockBoardAsync(payload),
                PacketId.CharacterEnhanceExitRequest => await commands.EnhanceAsync(payload),
                PacketId.GachaRequest => await DrawAsync(commands, payload),
                PacketId.TheaterEnterRequest => await EnterTheaterAsync(),
                PacketId.DebugGrantCurrencyRequest => await GrantDebugCurrencyAsync(),
                _ => [],
            };
        }
        catch
        {
            // Redis 타임아웃은 미적용을 뜻하지 않는다. 다음 호출에서 실제 상태와 저널을 다시 읽는다.
            _progress = null;
            throw;
        }
    }

    private async Task<byte[]> DrawAsync(ProgressCommands commands, byte[] payload)
    {
        var response = await commands.GachaAsync(payload);
        _progress = null;
        return response;
    }

    private async Task<byte[]> EnterTheaterAsync()
    {
        var currency = await CurrencyRepository.Instance.GetAsync(PlayerId);
        _snapshot = new TheaterSnapshot
        {
            CombatPower = await PlayerStatRepository.Instance.GetCombatPowerAsync(PlayerId),
            Gold = currency.Gold, Elleaf = currency.Elleaf, Macaron = currency.Macaron
        };
        _lastTouchAt = DateTime.UtcNow;
        return PacketWriter.Build(PacketId.TheaterEnterResult, [1]);
    }

    private async Task<byte[]> ExitTheaterAsync()
    {
        if (_snapshot is null) return PacketWriter.Build(PacketId.TheaterExitResult, [0]);
        var saved = _snapshot;
        bool tampered = await TheaterRecovery.RestoreAsync(PlayerId, saved);
        ClearTheater();
        var payload = new byte[33];
        payload[0] = tampered ? (byte)1 : (byte)0;
        BitConverter.TryWriteBytes(payload.AsSpan(1), saved.CombatPower);
        BitConverter.TryWriteBytes(payload.AsSpan(9), saved.Gold);
        BitConverter.TryWriteBytes(payload.AsSpan(17), saved.Elleaf);
        BitConverter.TryWriteBytes(payload.AsSpan(25), saved.Macaron);
        return PacketWriter.Build(PacketId.TheaterExitResult, payload);
    }

    private async Task<byte[]> GrantDebugCurrencyAsync()
    {
        if (!Config.ServerConfig.Instance.EnableDebugCommands) return [];
        await CurrencyRepository.Instance.IncrementGoldAsync(PlayerId, 5000);
        await CurrencyRepository.Instance.IncrementMacaronAsync(PlayerId, 5000);
        var currency = await CurrencyRepository.Instance.GetAsync(PlayerId);
        var result = new byte[17];
        result[0] = 1;
        BitConverter.TryWriteBytes(result.AsSpan(1), currency.Gold);
        BitConverter.TryWriteBytes(result.AsSpan(9), currency.Macaron);
        return PacketWriter.Build(PacketId.DebugGrantCurrencyResult, result);
    }

    private void ClearTheater() { _snapshot = null; _lastTouchAt = null; }
}
