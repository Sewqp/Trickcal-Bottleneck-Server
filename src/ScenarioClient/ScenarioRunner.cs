using System.Net.Sockets;
using System.Text;
using GameServer.Packet;

namespace ScenarioClient;

// 원래 Program.cs 최상단 스크립트였던 단일 플레이어 시나리오(로그인~길드)를, --players로 여러 명을
// 동시에 돌릴 수 있게 인스턴스 메서드로 추출한 것. 플레이어 하나의 실패가 나머지에 영향 주지 않도록
// 예외는 RunAsync에서만 잡고 ScenarioStats.IncrementFailed()로 집계.
public sealed class ScenarioRunner
{
    private readonly int _index;
    private readonly ScenarioOptions _options;
    private readonly string _playerName;
    private readonly string _tag;

    public ScenarioRunner(int index, ScenarioOptions options, string playerName)
    {
        _index = index;
        _options = options;
        _playerName = playerName;
        _tag = $"[p{index}]";
    }

    public static Task RunOnePlayerAsync(int index, ScenarioOptions options, CancellationToken ct)
    {
        string playerName = options.PlayerCount == 1
            ? options.PlayerNamePrefix
            : $"{options.PlayerNamePrefix}_{index}_{Guid.NewGuid():N}"[..20];
        return new ScenarioRunner(index, options, playerName).RunAsync(ct);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(_options.Host, _options.Port, ct);
            var stream = client.GetStream();
            Console.WriteLine($"{_tag} connected as '{_playerName}'");

            long playerId = await LoginAsync(stream, ct);
            Console.WriteLine($"{_tag}[Login] playerId={playerId}");

            int? targetCharacterId = await RequestCharacterListAsync(stream, ct);
            targetCharacterId = await GachaAsync(stream, ct) ?? targetCharacterId;

            if (targetCharacterId is int enhanceTarget)
                await EnhanceAsync(stream, ct, enhanceTarget);
            else
                Console.WriteLine($"{_tag}[Enhance] 대상 캐릭터 없음 — 건너뜀");

            if (targetCharacterId is int boardTarget)
                await BoardAsync(stream, ct, boardTarget);
            else
                Console.WriteLine($"{_tag}[Board] 대상 캐릭터 없음 — 건너뜀");

            await TheaterAsync(stream, ct);
            await GuildAsync(stream, ct);

            Console.WriteLine($"{_tag} 시나리오 완료");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ScenarioStats.IncrementFailed();
            Console.WriteLine($"{_tag} 실패: {ex.Message}");
        }
    }

    private async Task<long> LoginAsync(NetworkStream stream, CancellationToken ct)
    {
        var nameBytes = Encoding.UTF8.GetBytes(_playerName);
        var payload = new byte[2 + nameBytes.Length];
        BitConverter.TryWriteBytes(payload.AsSpan(0), (ushort)nameBytes.Length);
        nameBytes.CopyTo(payload.AsSpan(2));
        await stream.WriteAsync(PacketWriter.Build(PacketId.LoginRequest, payload), ct);

        var (id, result) = await PacketReader.ReadPacketAsync(stream, ct);
        if (id != PacketId.LoginResult || result.Length < 9 || result[0] != 1)
            throw new IOException("로그인 실패");

        ScenarioStats.IncrementLoginOk();
        return BitConverter.ToInt64(result.AsSpan(1));
    }

    private async Task<int?> RequestCharacterListAsync(NetworkStream stream, CancellationToken ct)
    {
        await stream.WriteAsync(PacketWriter.Build(PacketId.CharacterListRequest), ct);
        var (id, result) = await PacketReader.ReadPacketAsync(stream, ct);
        if (id != PacketId.CharacterListResult || result.Length < 2) return null;

        ushort count = BitConverter.ToUInt16(result.AsSpan(0));
        Console.WriteLine($"{_tag}[CharacterList] count={count}");

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

    private async Task<int?> GachaAsync(NetworkStream stream, CancellationToken ct)
    {
        await stream.WriteAsync(PacketWriter.Build(PacketId.GachaRequest), ct);
        var (id, result) = await PacketReader.ReadPacketAsync(stream, ct);
        if (id != PacketId.GachaResult || result.Length < 1 || result[0] == 0)
        {
            Console.WriteLine($"{_tag}[Gacha] 실패(잔액 부족 또는 처리 중)");
            return null;
        }

        ScenarioStats.IncrementGachaOk();
        ushort compensation = BitConverter.ToUInt16(result.AsSpan(1));
        Console.WriteLine($"{_tag}[Gacha] 성공, 보상 엘리프={compensation}");

        int? firstNew = null;
        int offset = 3;
        for (int i = 0; i < 10; i++)
        {
            int characterId = BitConverter.ToInt32(result.AsSpan(offset)); offset += 4;
            bool isNew = result[offset] == 1; offset += 1;
            if (isNew && firstNew == null) firstNew = characterId;
        }
        return firstNew;
    }

    private async Task EnhanceAsync(NetworkStream stream, CancellationToken ct, int characterId)
    {
        var enterPayload = new byte[4];
        BitConverter.TryWriteBytes(enterPayload.AsSpan(0), characterId);
        await stream.WriteAsync(PacketWriter.Build(PacketId.CharacterEnhanceEnterRequest, enterPayload), ct);

        var (enterId, enterResult) = await PacketReader.ReadPacketAsync(stream, ct);
        if (enterId != PacketId.CharacterEnhanceEnterResult || enterResult.Length < 16)
        {
            Console.WriteLine($"{_tag}[Enhance] 진입 응답 이상");
            return;
        }
        int level = BitConverter.ToInt32(enterResult.AsSpan(4));

        var exitPayload = new byte[8];
        BitConverter.TryWriteBytes(exitPayload.AsSpan(0), characterId);
        BitConverter.TryWriteBytes(exitPayload.AsSpan(4), level + 1);
        await stream.WriteAsync(PacketWriter.Build(PacketId.CharacterEnhanceExitRequest, exitPayload), ct);

        var (exitId, exitResult) = await PacketReader.ReadPacketAsync(stream, ct);
        if (exitId != PacketId.CharacterEnhanceExitResult || exitResult.Length < 13)
        {
            Console.WriteLine($"{_tag}[Enhance] 이탈 응답 이상");
            return;
        }
        bool success = exitResult[0] == 1;
        if (success) ScenarioStats.IncrementEnhanceOk();
        Console.WriteLine($"{_tag}[Enhance] success={success} level={BitConverter.ToInt32(exitResult.AsSpan(1))}");
    }

    private async Task BoardAsync(NetworkStream stream, CancellationToken ct, int characterId)
    {
        var listPayload = new byte[4];
        BitConverter.TryWriteBytes(listPayload.AsSpan(0), characterId);
        await stream.WriteAsync(PacketWriter.Build(PacketId.BoardListRequest, listPayload), ct);

        var (listId, listResult) = await PacketReader.ReadPacketAsync(stream, ct);
        if (listId != PacketId.BoardListResult || listResult.Length < 2)
        {
            Console.WriteLine($"{_tag}[Board] 목록 응답 이상");
            return;
        }

        ushort personalCount = BitConverter.ToUInt16(listResult.AsSpan(0));

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
            Console.WriteLine($"{_tag}[Board] 해금 가능한 보드 없음");
            return;
        }

        var unlockPayload = new byte[9];
        BitConverter.TryWriteBytes(unlockPayload.AsSpan(0), characterId);
        BitConverter.TryWriteBytes(unlockPayload.AsSpan(4), boardToUnlock);
        unlockPayload[8] = 0; // isGlobal=false
        await stream.WriteAsync(PacketWriter.Build(PacketId.BoardUnlockRequest, unlockPayload), ct);

        var (unlockId, unlockResult) = await PacketReader.ReadPacketAsync(stream, ct);
        bool success = unlockId == PacketId.BoardUnlockResult && unlockResult.Length >= 1 && unlockResult[0] == 1;
        if (success) ScenarioStats.IncrementBoardUnlockOk();
        Console.WriteLine($"{_tag}[Board] boardNo={boardToUnlock} unlock success={success}");
    }

    private async Task TheaterAsync(NetworkStream stream, CancellationToken ct)
    {
        await stream.WriteAsync(PacketWriter.Build(PacketId.TheaterEnterRequest), ct);
        var (enterId, enterResult) = await PacketReader.ReadPacketAsync(stream, ct);
        bool entered = enterId == PacketId.TheaterEnterResult && enterResult.Length >= 1 && enterResult[0] == 1;
        if (!entered)
        {
            Console.WriteLine($"{_tag}[Theater] 진입 실패");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(_options.StoryDurationSeconds), ct);

        await stream.WriteAsync(PacketWriter.Build(PacketId.TheaterExitRequest), ct);
        var (exitId, exitResult) = await PacketReader.ReadPacketAsync(stream, ct);
        if (exitId != PacketId.TheaterExitResult || exitResult.Length < 33)
        {
            Console.WriteLine($"{_tag}[Theater] 퇴장 거부(위장 진입 등)");
            return;
        }

        bool tampered = exitResult[0] == 1;
        if (tampered) ScenarioStats.IncrementTheaterTampered();
        else ScenarioStats.IncrementTheaterOk();
        Console.WriteLine($"{_tag}[Theater] 퇴장 tampered={tampered}");
    }

    private async Task GuildAsync(NetworkStream stream, CancellationToken ct)
    {
        var nameBytes = Encoding.UTF8.GetBytes(_options.GuildName);
        var createPayload = new byte[2 + nameBytes.Length];
        BitConverter.TryWriteBytes(createPayload.AsSpan(0), (ushort)nameBytes.Length);
        nameBytes.CopyTo(createPayload.AsSpan(2));
        await stream.WriteAsync(PacketWriter.Build(PacketId.GuildCreateRequest, createPayload), ct);

        var (createId, createResult) = await PacketReader.ReadPacketAsync(stream, ct);
        if (createId != PacketId.GuildCreateResult || createResult.Length < 9 || createResult[0] != 1)
        {
            Console.WriteLine($"{_tag}[Guild] 생성 실패(이름 중복 등)");
            return;
        }

        ScenarioStats.IncrementGuildCreateOk();
        long guildId = BitConverter.ToInt64(createResult.AsSpan(1));
        Console.WriteLine($"{_tag}[Guild] 생성 성공 guildId={guildId}");
    }
}
