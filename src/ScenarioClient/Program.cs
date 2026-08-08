using System.Net.Sockets;
using System.Text;
using GameServer.Packet;
using ScenarioClient;

var options = ScenarioOptions.Parse(args);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
var ct = cts.Token;

using var client = new TcpClient();
await client.ConnectAsync(options.Host, options.Port, ct);
var stream = client.GetStream();
Console.WriteLine($"[ScenarioClient] connected to {options.Host}:{options.Port} as '{options.PlayerName}'");

// ── 1. 로그인 ──────────────────────────────────────────────
long playerId = await LoginAsync();

// ── 2. 캐릭터 목록 조회 ────────────────────────────────────
int? targetCharacterId = await RequestCharacterListAsync();

// ── 3. 가챠 10연챠 (1000엘리프) ────────────────────────────
targetCharacterId = await GachaAsync() ?? targetCharacterId;

// ── 4. 캐릭터 강화 (마카롱) ────────────────────────────────
if (targetCharacterId is int enhanceTarget)
    await EnhanceAsync(enhanceTarget);
else
    Console.WriteLine("[Enhance] 대상 캐릭터 없음 — 건너뜀");

// ── 5. 보드 조회 + 해금 (골드) ─────────────────────────────
if (targetCharacterId is int boardTarget)
    await BoardAsync(boardTarget);
else
    Console.WriteLine("[Board] 대상 캐릭터 없음 — 건너뜀");

// ── 6. 극장(스토리) 진입 → 대기 → 퇴장 ─────────────────────
await TheaterAsync();

// ── 7. 길드 생성 + 정보 조회 ───────────────────────────────
await GuildAsync();

Console.WriteLine("[ScenarioClient] 시나리오 완료");
return;

async Task<long> LoginAsync()
{
    var nameBytes = Encoding.UTF8.GetBytes(options.PlayerName);
    var payload = new byte[2 + nameBytes.Length];
    BitConverter.TryWriteBytes(payload.AsSpan(0), (ushort)nameBytes.Length);
    nameBytes.CopyTo(payload.AsSpan(2));
    await stream.WriteAsync(PacketWriter.Build(PacketId.LoginRequest, payload), ct);

    var (id, result) = await PacketReader.ReadPacketAsync(stream, ct);
    if (id != PacketId.LoginResult || result.Length < 9 || result[0] != 1)
        throw new IOException("로그인 실패");

    long pid = BitConverter.ToInt64(result.AsSpan(1));
    Console.WriteLine($"[Login] playerId={pid}");
    return pid;
}

async Task<int?> RequestCharacterListAsync()
{
    await stream.WriteAsync(PacketWriter.Build(PacketId.CharacterListRequest), ct);
    var (id, result) = await PacketReader.ReadPacketAsync(stream, ct);
    if (id != PacketId.CharacterListResult || result.Length < 2) return null;

    ushort count = BitConverter.ToUInt16(result.AsSpan(0));
    Console.WriteLine($"[CharacterList] count={count}");

    int? owned = null;
    int offset = 2;
    for (int i = 0; i < count; i++)
    {
        int characterId = BitConverter.ToInt32(result.AsSpan(offset)); offset += 4;
        byte status = result[offset]; offset += 1;
        offset += 4; // level — 목록 출력에선 안 씀
        if (status == 1 && owned == null) owned = characterId;
    }
    return owned;
}

async Task<int?> GachaAsync()
{
    await stream.WriteAsync(PacketWriter.Build(PacketId.GachaRequest), ct);
    var (id, result) = await PacketReader.ReadPacketAsync(stream, ct);
    if (id != PacketId.GachaResult || result.Length < 1 || result[0] == 0)
    {
        Console.WriteLine("[Gacha] 실패(잔액 부족 또는 처리 중)");
        return null;
    }

    ushort compensation = BitConverter.ToUInt16(result.AsSpan(1));
    Console.WriteLine($"[Gacha] 성공, 보상 엘리프={compensation}");

    int? firstNew = null;
    int offset = 3;
    for (int i = 0; i < 10; i++)
    {
        int characterId = BitConverter.ToInt32(result.AsSpan(offset)); offset += 4;
        bool isNew = result[offset] == 1; offset += 1;
        Console.WriteLine($"  - characterId={characterId} isNew={isNew}");
        if (isNew && firstNew == null) firstNew = characterId;
    }
    return firstNew;
}

async Task EnhanceAsync(int characterId)
{
    var enterPayload = new byte[4];
    BitConverter.TryWriteBytes(enterPayload.AsSpan(0), characterId);
    await stream.WriteAsync(PacketWriter.Build(PacketId.CharacterEnhanceEnterRequest, enterPayload), ct);

    var (enterId, enterResult) = await PacketReader.ReadPacketAsync(stream, ct);
    if (enterId != PacketId.CharacterEnhanceEnterResult || enterResult.Length < 16)
    {
        Console.WriteLine("[Enhance] 진입 응답 이상");
        return;
    }
    int level = BitConverter.ToInt32(enterResult.AsSpan(4));
    long macaron = BitConverter.ToInt64(enterResult.AsSpan(8));
    Console.WriteLine($"[Enhance] 진입 characterId={characterId} level={level} macaron={macaron}");

    var exitPayload = new byte[8];
    BitConverter.TryWriteBytes(exitPayload.AsSpan(0), characterId);
    BitConverter.TryWriteBytes(exitPayload.AsSpan(4), level + 1);
    await stream.WriteAsync(PacketWriter.Build(PacketId.CharacterEnhanceExitRequest, exitPayload), ct);

    var (exitId, exitResult) = await PacketReader.ReadPacketAsync(stream, ct);
    if (exitId != PacketId.CharacterEnhanceExitResult || exitResult.Length < 13)
    {
        Console.WriteLine("[Enhance] 이탈 응답 이상");
        return;
    }
    bool success = exitResult[0] == 1;
    int finalLevel = BitConverter.ToInt32(exitResult.AsSpan(1));
    long remainMacaron = BitConverter.ToInt64(exitResult.AsSpan(5));
    Console.WriteLine($"[Enhance] 이탈 success={success} level={finalLevel} macaron={remainMacaron}");
}

async Task BoardAsync(int characterId)
{
    var listPayload = new byte[4];
    BitConverter.TryWriteBytes(listPayload.AsSpan(0), characterId);
    await stream.WriteAsync(PacketWriter.Build(PacketId.BoardListRequest, listPayload), ct);

    var (listId, listResult) = await PacketReader.ReadPacketAsync(stream, ct);
    if (listId != PacketId.BoardListResult || listResult.Length < 2)
    {
        Console.WriteLine("[Board] 목록 응답 이상");
        return;
    }

    ushort personalCount = BitConverter.ToUInt16(listResult.AsSpan(0));
    Console.WriteLine($"[Board] characterId={characterId} personalBoardCount={personalCount}");

    int? lockedBoardNo = null;
    int offset = 2;
    for (int i = 0; i < personalCount; i++)
    {
        int boardNo = BitConverter.ToInt32(listResult.AsSpan(offset)); offset += 4;
        byte status = listResult[offset]; offset += 1;
        if (status == 0 && lockedBoardNo == null) lockedBoardNo = boardNo;
    }

    if (lockedBoardNo is not int boardToUnlock)
    {
        Console.WriteLine("[Board] 해금 가능한 보드 없음");
        return;
    }

    var unlockPayload = new byte[9];
    BitConverter.TryWriteBytes(unlockPayload.AsSpan(0), characterId);
    BitConverter.TryWriteBytes(unlockPayload.AsSpan(4), boardToUnlock);
    unlockPayload[8] = 0; // isGlobal=false
    await stream.WriteAsync(PacketWriter.Build(PacketId.BoardUnlockRequest, unlockPayload), ct);

    var (unlockId, unlockResult) = await PacketReader.ReadPacketAsync(stream, ct);
    bool success = unlockId == PacketId.BoardUnlockResult && unlockResult.Length >= 1 && unlockResult[0] == 1;
    Console.WriteLine($"[Board] boardNo={boardToUnlock} unlock success={success}");
}

async Task TheaterAsync()
{
    await stream.WriteAsync(PacketWriter.Build(PacketId.TheaterEnterRequest), ct);
    var (enterId, enterResult) = await PacketReader.ReadPacketAsync(stream, ct);
    bool entered = enterId == PacketId.TheaterEnterResult && enterResult.Length >= 1 && enterResult[0] == 1;
    Console.WriteLine($"[Theater] 진입 success={entered}");
    if (!entered) return;

    Console.WriteLine($"[Theater] 스토리 재생 중... ({options.StoryDurationSeconds}s)");
    await Task.Delay(TimeSpan.FromSeconds(options.StoryDurationSeconds), ct);

    await stream.WriteAsync(PacketWriter.Build(PacketId.TheaterExitRequest), ct);
    var (exitId, exitResult) = await PacketReader.ReadPacketAsync(stream, ct);
    if (exitId != PacketId.TheaterExitResult || exitResult.Length < 33)
    {
        Console.WriteLine("[Theater] 퇴장 거부(위장 진입 등)");
        return;
    }

    bool tampered = exitResult[0] == 1;
    long combatPower = BitConverter.ToInt64(exitResult.AsSpan(1));
    long gold = BitConverter.ToInt64(exitResult.AsSpan(9));
    long elleaf = BitConverter.ToInt64(exitResult.AsSpan(17));
    long macaron = BitConverter.ToInt64(exitResult.AsSpan(25));
    Console.WriteLine($"[Theater] 퇴장 tampered={tampered} combatPower={combatPower} gold={gold} elleaf={elleaf} macaron={macaron}");
}

async Task GuildAsync()
{
    var nameBytes = Encoding.UTF8.GetBytes(options.GuildName);
    var createPayload = new byte[2 + nameBytes.Length];
    BitConverter.TryWriteBytes(createPayload.AsSpan(0), (ushort)nameBytes.Length);
    nameBytes.CopyTo(createPayload.AsSpan(2));
    await stream.WriteAsync(PacketWriter.Build(PacketId.GuildCreateRequest, createPayload), ct);

    var (createId, createResult) = await PacketReader.ReadPacketAsync(stream, ct);
    if (createId != PacketId.GuildCreateResult || createResult.Length < 9 || createResult[0] != 1)
    {
        Console.WriteLine("[Guild] 생성 실패(이름 중복 등)");
        return;
    }
    long guildId = BitConverter.ToInt64(createResult.AsSpan(1));
    Console.WriteLine($"[Guild] 생성 성공 guildId={guildId} name={options.GuildName}");

    var infoPayload = new byte[8];
    BitConverter.TryWriteBytes(infoPayload.AsSpan(0), guildId);
    await stream.WriteAsync(PacketWriter.Build(PacketId.GuildInfoRequest, infoPayload), ct);

    var (infoId, infoResult) = await PacketReader.ReadPacketAsync(stream, ct);
    if (infoId != PacketId.GuildInfoResult || infoResult.Length < 1 || infoResult[0] != 1)
    {
        Console.WriteLine("[Guild] 정보 조회 실패");
        return;
    }

    ushort nameLen = BitConverter.ToUInt16(infoResult.AsSpan(1));
    string name = Encoding.UTF8.GetString(infoResult.AsSpan(3, nameLen));
    ushort memberCount = BitConverter.ToUInt16(infoResult.AsSpan(3 + nameLen));
    Console.WriteLine($"[Guild] 정보 name={name} memberCount={memberCount}");
}
