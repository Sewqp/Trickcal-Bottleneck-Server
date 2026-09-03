using System.Text;
using GameServer.Packet;

namespace GameClient;

// 사람이 메뉴로 하나씩 골라가며 조작하는 실제 플레이 세션. ScenarioClient(고정 순서 자동 시나리오)와
// 같은 패킷 포맷을 그대로 쓰지만, 매 요청을 사용자 입력 시점에 개별적으로 보내고 결과를 화면에
// 그대로 보여준다는 점이 다르다.
public sealed class GameSession
{
    private readonly PacketChannel _channel;
    public long PlayerId { get; private set; }
    public string PlayerName { get; }

    public GameSession(PacketChannel channel, string playerName)
    {
        _channel = channel;
        PlayerName = playerName;
    }

    public async Task<bool> LoginAsync(CancellationToken ct)
    {
        var nameBytes = Encoding.UTF8.GetBytes(PlayerName);
        var payload = new byte[2 + nameBytes.Length];
        BitConverter.TryWriteBytes(payload.AsSpan(0), (ushort)nameBytes.Length);
        nameBytes.CopyTo(payload.AsSpan(2));
        await _channel.SendAsync(PacketWriter.Build(PacketId.LoginRequest, payload), ct);

        var (id, result) = await _channel.ReadResponseAsync(ct);
        if (id != PacketId.LoginResult || result.Length < 9 || result[0] != 1)
            return false;

        PlayerId = BitConverter.ToInt64(result.AsSpan(1));
        return true;
    }

    public async Task SendHeartbeatAsync(CancellationToken ct)
        => await _channel.SendAsync(PacketWriter.Build(PacketId.Heartbeat), ct);

    public async Task<List<(int CharacterId, byte Status, int Level)>> GetCharacterListAsync(CancellationToken ct)
    {
        await _channel.SendAsync(PacketWriter.Build(PacketId.CharacterListRequest), ct);
        var (id, result) = await _channel.ReadResponseAsync(ct);
        var list = new List<(int, byte, int)>();
        if (id != PacketId.CharacterListResult || result.Length < 2) return list;

        ushort count = BitConverter.ToUInt16(result.AsSpan(0));
        int offset = 2;
        for (int i = 0; i < count; i++)
        {
            int characterId = BitConverter.ToInt32(result.AsSpan(offset)); offset += 4;
            byte status = result[offset]; offset += 1;
            int level = BitConverter.ToInt32(result.AsSpan(offset)); offset += 4;
            list.Add((characterId, status, level));
        }
        return list;
    }

    public async Task<(bool Success, long Compensation, List<(int CharacterId, bool IsNew)> Draws)> GachaAsync(CancellationToken ct)
    {
        await _channel.SendAsync(PacketWriter.Build(PacketId.GachaRequest), ct);
        var (id, result) = await _channel.ReadResponseAsync(ct);
        if (id != PacketId.GachaResult || result.Length < 1 || result[0] == 0)
            return (false, 0, []);

        ushort compensation = BitConverter.ToUInt16(result.AsSpan(1));
        var draws = new List<(int, bool)>(10);
        int offset = 3;
        for (int i = 0; i < 10; i++)
        {
            int characterId = BitConverter.ToInt32(result.AsSpan(offset)); offset += 4;
            bool isNew = result[offset] == 1; offset += 1;
            draws.Add((characterId, isNew));
        }
        return (true, compensation, draws);
    }

    public async Task<(int Level, long Macaron, long Gold)> EnhanceEnterAsync(int characterId, CancellationToken ct)
    {
        var payload = new byte[4];
        BitConverter.TryWriteBytes(payload.AsSpan(0), characterId);
        await _channel.SendAsync(PacketWriter.Build(PacketId.CharacterEnhanceEnterRequest, payload), ct);

        var (id, result) = await _channel.ReadResponseAsync(ct);
        if (id != PacketId.CharacterEnhanceEnterResult || result.Length < 24) return (-1, 0, 0);
        return (BitConverter.ToInt32(result.AsSpan(4)), BitConverter.ToInt64(result.AsSpan(8)), BitConverter.ToInt64(result.AsSpan(16)));
    }

    public async Task<(bool Success, int Level, long Macaron, long Gold, long MacaronSpent, long GoldSpent)> EnhanceExitAsync(
        int characterId, int targetLevel, CancellationToken ct)
    {
        var payload = new byte[8];
        BitConverter.TryWriteBytes(payload.AsSpan(0), characterId);
        BitConverter.TryWriteBytes(payload.AsSpan(4), targetLevel);
        await _channel.SendAsync(PacketWriter.Build(PacketId.CharacterEnhanceExitRequest, payload), ct);

        var (id, result) = await _channel.ReadResponseAsync(ct);
        if (id != PacketId.CharacterEnhanceExitResult || result.Length < 37) return (false, -1, 0, 0, 0, 0);
        return (
            result[0] == 1,
            BitConverter.ToInt32(result.AsSpan(1)),
            BitConverter.ToInt64(result.AsSpan(5)),
            BitConverter.ToInt64(result.AsSpan(13)),
            BitConverter.ToInt64(result.AsSpan(21)),
            BitConverter.ToInt64(result.AsSpan(29)));
    }

    // 테스트 더미 전용 치트 — 어드민 세션에서만 정상 동작해야 하지만 서버 쪽 DebugHandler에
    // 그 체크가 빠져 있어(의도된 버그) 누구나 호출하면 재화를 받는다. 응답은 정상적으로 오지만,
    // 어드민이 아니면 서버가 응답 전송 직후 예외를 던져 연결이 끊긴다 — apm이 이 요청을 500으로
    // 잡는지 보는 테스트용.
    public async Task<(bool Success, long Gold, long Macaron)> DebugGrantCurrencyAsync(CancellationToken ct)
    {
        await _channel.SendAsync(PacketWriter.Build(PacketId.DebugGrantCurrencyRequest), ct);
        var (id, result) = await _channel.ReadResponseAsync(ct);
        if (id != PacketId.DebugGrantCurrencyResult || result.Length < 17 || result[0] != 1) return (false, 0, 0);
        return (true, BitConverter.ToInt64(result.AsSpan(1)), BitConverter.ToInt64(result.AsSpan(9)));
    }

    public async Task<(List<(int BoardNo, byte Status)> Personal, List<(int BoardNo, byte Status)> Global)> GetBoardListAsync(
        int characterId, CancellationToken ct)
    {
        var payload = new byte[4];
        BitConverter.TryWriteBytes(payload.AsSpan(0), characterId);
        await _channel.SendAsync(PacketWriter.Build(PacketId.BoardListRequest, payload), ct);

        var (id, result) = await _channel.ReadResponseAsync(ct);
        var personal = new List<(int, byte)>();
        var global = new List<(int, byte)>();
        if (id != PacketId.BoardListResult || result.Length < 2) return (personal, global);

        int offset = 0;
        ushort personalCount = BitConverter.ToUInt16(result.AsSpan(offset)); offset += 2;
        for (int i = 0; i < personalCount; i++)
        {
            int boardNo = BitConverter.ToInt32(result.AsSpan(offset)); offset += 4;
            byte status = result[offset]; offset += 1;
            personal.Add((boardNo, status));
        }

        ushort globalCount = BitConverter.ToUInt16(result.AsSpan(offset)); offset += 2;
        for (int i = 0; i < globalCount; i++)
        {
            int boardNo = BitConverter.ToInt32(result.AsSpan(offset)); offset += 4;
            byte status = result[offset]; offset += 1;
            global.Add((boardNo, status));
        }

        return (personal, global);
    }

    public async Task<bool> UnlockBoardAsync(int characterId, int boardNo, bool isGlobal, CancellationToken ct)
    {
        var payload = new byte[9];
        BitConverter.TryWriteBytes(payload.AsSpan(0), characterId);
        BitConverter.TryWriteBytes(payload.AsSpan(4), boardNo);
        payload[8] = (byte)(isGlobal ? 1 : 0);
        await _channel.SendAsync(PacketWriter.Build(PacketId.BoardUnlockRequest, payload), ct);

        var (id, result) = await _channel.ReadResponseAsync(ct);
        return id == PacketId.BoardUnlockResult && result.Length >= 1 && result[0] == 1;
    }

    public async Task<bool> TheaterEnterAsync(CancellationToken ct)
    {
        await _channel.SendAsync(PacketWriter.Build(PacketId.TheaterEnterRequest), ct);
        var (id, result) = await _channel.ReadResponseAsync(ct);
        return id == PacketId.TheaterEnterResult && result.Length >= 1 && result[0] == 1;
    }

    public async Task TheaterTouchAsync(CancellationToken ct)
        => await _channel.SendAsync(PacketWriter.Build(PacketId.TheaterTouchRequest), ct);

    public async Task<(bool Tampered, long CombatPower, long Gold, long Elleaf, long Macaron)> TheaterExitAsync(CancellationToken ct)
    {
        await _channel.SendAsync(PacketWriter.Build(PacketId.TheaterExitRequest), ct);
        var (id, result) = await _channel.ReadResponseAsync(ct);
        if (id != PacketId.TheaterExitResult || result.Length < 33) return (true, 0, 0, 0, 0);

        return (
            result[0] == 1,
            BitConverter.ToInt64(result.AsSpan(1)),
            BitConverter.ToInt64(result.AsSpan(9)),
            BitConverter.ToInt64(result.AsSpan(17)),
            BitConverter.ToInt64(result.AsSpan(25)));
    }

    public async Task<long?> MatchRequestAsync(TimeSpan timeout, CancellationToken ct)
    {
        _channel.DrainStaleMatchResults();
        await _channel.SendAsync(PacketWriter.Build(PacketId.MatchRequest), ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var payload = await _channel.ReadMatchResultAsync(timeoutCts.Token);
            if (payload.Length >= 9 && payload[0] == 1)
                return BitConverter.ToInt64(payload.AsSpan(1));
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 상대 없음(타임아웃) — 서버 큐에 남지 않도록 취소 요청. 안 보내면 나중에 접속한 다른
            // 플레이어가 이미 포기한 이 요청과 "매칭 성사"로 잘못 묶여버림(죽은 세션과 매칭되는 버그).
            await _channel.SendAsync(PacketWriter.Build(PacketId.MatchCancelRequest), ct);
            return null;
        }
    }

    public async Task<long?> GuildCreateAsync(string guildName, CancellationToken ct)
    {
        var nameBytes = Encoding.UTF8.GetBytes(guildName);
        var payload = new byte[2 + nameBytes.Length];
        BitConverter.TryWriteBytes(payload.AsSpan(0), (ushort)nameBytes.Length);
        nameBytes.CopyTo(payload.AsSpan(2));
        await _channel.SendAsync(PacketWriter.Build(PacketId.GuildCreateRequest, payload), ct);

        var (id, result) = await _channel.ReadResponseAsync(ct);
        if (id != PacketId.GuildCreateResult || result.Length < 9 || result[0] != 1) return null;
        return BitConverter.ToInt64(result.AsSpan(1));
    }

    public async Task<bool> GuildJoinAsync(long guildId, CancellationToken ct)
    {
        var payload = new byte[8];
        BitConverter.TryWriteBytes(payload.AsSpan(0), guildId);
        await _channel.SendAsync(PacketWriter.Build(PacketId.GuildJoinRequest, payload), ct);

        var (id, result) = await _channel.ReadResponseAsync(ct);
        return id == PacketId.GuildJoinResult && result.Length >= 1 && result[0] == 1;
    }

    public async Task<bool> GuildLeaveAsync(long guildId, CancellationToken ct)
    {
        var payload = new byte[8];
        BitConverter.TryWriteBytes(payload.AsSpan(0), guildId);
        await _channel.SendAsync(PacketWriter.Build(PacketId.GuildLeaveRequest, payload), ct);

        var (id, result) = await _channel.ReadResponseAsync(ct);
        return id == PacketId.GuildLeaveResult && result.Length >= 1 && result[0] == 1;
    }

    public async Task<(bool Success, string Name, List<(long PlayerId, byte Role)> Members)> GuildInfoAsync(
        long guildId, CancellationToken ct)
    {
        var payload = new byte[8];
        BitConverter.TryWriteBytes(payload.AsSpan(0), guildId);
        await _channel.SendAsync(PacketWriter.Build(PacketId.GuildInfoRequest, payload), ct);

        var (id, result) = await _channel.ReadResponseAsync(ct);
        var members = new List<(long, byte)>();
        if (id != PacketId.GuildInfoResult || result.Length < 1 || result[0] != 1)
            return (false, "", members);

        ushort nameLen = BitConverter.ToUInt16(result.AsSpan(1));
        string name = Encoding.UTF8.GetString(result.AsSpan(3, nameLen));
        int offset = 3 + nameLen;
        ushort memberCount = BitConverter.ToUInt16(result.AsSpan(offset)); offset += 2;
        for (int i = 0; i < memberCount; i++)
        {
            long playerId = BitConverter.ToInt64(result.AsSpan(offset)); offset += 8;
            byte role = result[offset]; offset += 1;
            members.Add((playerId, role));
        }
        return (true, name, members);
    }
}
