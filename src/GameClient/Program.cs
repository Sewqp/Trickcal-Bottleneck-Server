using System.Net.Sockets;
using GameClient;

string host = "127.0.0.1";
int port = 9100;
string? name = null;

for (int i = 0; i < args.Length - 1; i += 2)
{
    switch (args[i])
    {
        case "--host": host = args[i + 1]; break;
        case "--port": port = int.Parse(args[i + 1]); break;
        case "--name": name = args[i + 1]; break;
    }
}

if (string.IsNullOrWhiteSpace(name))
{
    Console.Write("플레이어 이름: ");
    name = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(name)) name = $"player_{Guid.NewGuid():N}"[..12];
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
var ct = cts.Token;

using var client = new TcpClient();
Console.WriteLine($"[GameClient] {host}:{port} 접속 중...");
await client.ConnectAsync(host, port, ct);
var stream = client.GetStream();
var channel = new PacketChannel(stream);
channel.Start(ct);

var session = new GameSession(channel, name);
if (!await session.LoginAsync(ct))
{
    Console.WriteLine("로그인 실패");
    return;
}
Console.WriteLine($"[GameClient] 로그인 성공. playerId={session.PlayerId} name={name}");

// 메뉴에서 사람이 고민하는 동안에도 30초 하트비트 타임아웃(HeartbeatManager)에 걸리지 않도록
// 백그라운드에서 계속 생존 신호를 보낸다. ack는 PacketChannel이 별도 경로로 버리므로 메뉴 입출력과
// 경합하지 않는다.
_ = Task.Run(async () =>
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            await session.SendHeartbeatAsync(ct);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception)
        {
            break; // 연결 종료 등 — 메뉴 루프의 다음 요청이 알아서 실패로 드러남
        }
    }
});

long? currentGuildId = null;

try
{
    while (!ct.IsCancellationRequested)
    {
        PrintMenu();
        Console.Write("> ");
        string? choice = Console.ReadLine();
        Console.WriteLine();

        try
        {
            switch (choice)
            {
                case "1": await ShowCharacterListAsync(); break;
                case "2": await GachaAsync(); break;
                case "3": await EnhanceAsync(); break;
                case "4": await BoardAsync(); break;
                case "5": await TheaterAsync(); break;
                case "6": await MatchAsync(); break;
                case "7": await GuildMenuAsync(); break;
                case "8": await DebugGrantCurrencyAsync(); break;
                case "0": return;
                default: Console.WriteLine("잘못된 선택입니다."); break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[오류] {ex.Message}");
        }

        Console.WriteLine();
    }
}
catch (OperationCanceledException)
{
}

void PrintMenu()
{
    Console.WriteLine("==================================");
    Console.WriteLine($" {name} (playerId={session.PlayerId})");
    Console.WriteLine(" 1) 캐릭터 목록");
    Console.WriteLine(" 2) 가챠 (10연챠, 1000 엘리프)");
    Console.WriteLine(" 3) 캐릭터 강화 (레벨업)");
    Console.WriteLine(" 4) 보드 목록 / 해금");
    Console.WriteLine(" 5) 극장 (스토리 감상)");
    Console.WriteLine(" 6) 매칭 신청");
    Console.WriteLine(" 7) 길드");
    Console.WriteLine(" 8) [테스트] 골드/마카롱 획득 (어드민 전용, admin 계정 아니면 이후 연결이 끊깁니다)");
    Console.WriteLine(" 0) 종료");
    Console.WriteLine("==================================");
}

async Task ShowCharacterListAsync()
{
    var list = await session.GetCharacterListAsync(ct);
    if (list.Count == 0) { Console.WriteLine("캐릭터 정보를 불러오지 못했습니다."); return; }

    foreach (var (characterId, status, level) in list)
    {
        string owned = status == 1 ? "보유" : "미보유";
        Console.WriteLine($"  #{characterId} [{owned}] Lv.{level}");
    }
}

async Task GachaAsync()
{
    var (success, compensation, draws) = await session.GachaAsync(ct);
    if (!success) { Console.WriteLine("가챠 실패 (엘리프 부족 또는 처리 중)"); return; }

    foreach (var (characterId, isNew) in draws)
        Console.WriteLine($"  #{characterId} {(isNew ? "[신규]" : "[중복]")}");
    Console.WriteLine($"중복 보상 엘리프: {compensation}");
}

async Task<int?> PickOwnedCharacterAsync()
{
    var list = await session.GetCharacterListAsync(ct);
    var owned = list.Where(c => c.Status == 1).ToList();
    if (owned.Count == 0) { Console.WriteLine("보유한 캐릭터가 없습니다. 먼저 가챠를 진행하세요."); return null; }

    foreach (var (characterId, _, level) in owned)
        Console.WriteLine($"  #{characterId} Lv.{level}");
    Console.Write("캐릭터 ID 입력: ");
    if (!int.TryParse(Console.ReadLine(), out int id) || owned.All(c => c.CharacterId != id))
    {
        Console.WriteLine("잘못된 입력입니다.");
        return null;
    }
    return id;
}

// 서버 설계의 핵심(redis가 기준값, 실제 반영은 "UI 이탈" 시점 하나뿐)을 눈으로 보여주기 위한 로컬 루프.
// 여기서 +1은 전부 클라이언트 메모리 안에서만 증가하고 서버로는 아무것도 안 보낸다 — 실제 게임의
// "강화 버튼 여러 번 눌러도 로컬에서만 반영되다가 화면 나갈 때 한 번에 서버로 반영" 흐름을 흉내낸 것.
// 이 상태에서 창을 강제로 닫으면(Exit 요청이 끝내 안 나감) 로컬 누적분은 전부 사라지고, 재접속해서
// 캐릭터 목록을 보면 마지막으로 정상 "나가기"한 레벨 그대로임을 확인할 수 있다.
async Task EnhanceAsync()
{
    var characterId = await PickOwnedCharacterAsync();
    if (characterId is not int cid) return;

    var (level, macaron, gold) = await session.EnhanceEnterAsync(cid, ct);
    if (level < 0) { Console.WriteLine("강화 진입 실패"); return; }
    Console.WriteLine($"[강화 UI 진입] 현재 레벨: {level}, 보유 마카롱: {macaron}, 보유 골드: {gold}");
    Console.WriteLine("여기서 '+1'은 로컬에만 반영됩니다(서버 전송 없음) — '나가기'를 선택해야 서버에 실제로 저장돼요.");
    Console.WriteLine("(확인해보고 싶으면: +1 몇 번 누른 뒤 나가기 대신 창을 강제로 닫고 재접속해서 캐릭터 목록을 보세요)");

    int localLevel = level;
    while (true)
    {
        Console.WriteLine($"  로컬 레벨: {localLevel}{(localLevel > level ? " (미저장)" : "")}");
        Console.WriteLine("  1) +1 레벨업(로컬)   2) 나가기(서버에 저장)   3) 취소(변경 없이 나가기)");
        Console.Write("  > ");
        string? sub = Console.ReadLine();
        if (sub == "1") { localLevel++; continue; }
        if (sub == "2") break;
        if (sub == "3") { localLevel = level; break; }
        Console.WriteLine("  잘못된 입력입니다.");
    }

    if (localLevel == level)
    {
        Console.WriteLine("[강화 UI 이탈] 변경 없음 — 그래도 이탈 요청은 보냅니다(락 해제).");
        var (_, cancelLevel, cancelMacaron, cancelGold, _, _) = await session.EnhanceExitAsync(cid, level, ct);
        Console.WriteLine($"레벨 {cancelLevel}, 마카롱 {cancelMacaron}, 골드 {cancelGold} 유지");
        return;
    }

    Console.WriteLine($"[강화 UI 이탈] 로컬에서 누적한 목표 레벨 {localLevel}을 서버로 전송합니다...");
    var (success, finalLevel, finalMacaron, finalGold, macaronSpent, goldSpent) = await session.EnhanceExitAsync(cid, localLevel, ct);
    Console.WriteLine(success
        ? $"강화 성공! 레벨 {finalLevel}. 소모 마카롱 {macaronSpent}, 소모 골드 {goldSpent} (남은 마카롱 {finalMacaron}, 남은 골드 {finalGold})"
        : $"강화 실패(마카롱 또는 골드 부족). 레벨 {finalLevel} 유지, 마카롱 {finalMacaron}, 골드 {finalGold}");
}

async Task DebugGrantCurrencyAsync()
{
    var (success, gold, macaron) = await session.DebugGrantCurrencyAsync(ct);
    Console.WriteLine(success
        ? $"[테스트] 골드/마카롱 지급됨. 현재 골드={gold}, 마카롱={macaron}"
        : "지급 실패");
}

async Task BoardAsync()
{
    var characterId = await PickOwnedCharacterAsync();
    if (characterId is not int cid) return;

    var (personal, global) = await session.GetBoardListAsync(cid, ct);
    Console.WriteLine("[개인 보드]");
    foreach (var (no, status) in personal)
        Console.WriteLine($"  no.{no} {(status == 1 ? "해금됨" : "잠김")}");
    Console.WriteLine("[전체 보드]");
    foreach (var (no, status) in global)
        Console.WriteLine($"  no.{no} {(status == 1 ? "해금됨" : "잠김")}");

    Console.Write("해금할 보드 번호 (건너뛰려면 빈칸): ");
    string? input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input)) return;
    if (!int.TryParse(input, out int boardNo)) { Console.WriteLine("잘못된 입력입니다."); return; }

    Console.Write("전체 보드입니까? (y/N): ");
    bool isGlobal = (Console.ReadLine() ?? "").Trim().Equals("y", StringComparison.OrdinalIgnoreCase);

    bool success = await session.UnlockBoardAsync(cid, boardNo, isGlobal, ct);
    Console.WriteLine(success ? "해금 성공" : "해금 실패(골드 부족 또는 잘못된 보드)");
}

async Task TheaterAsync()
{
    if (!await session.TheaterEnterAsync(ct)) { Console.WriteLine("극장 진입 실패(이미 락 상태 등)"); return; }
    Console.WriteLine("극장 입장. 스토리 재생 중...");

    for (int i = 0; i < 3; i++)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        await session.TheaterTouchAsync(ct);
        Console.Write(".");
    }
    Console.WriteLine();

    var (tampered, combatPower, gold, elleaf, macaron) = await session.TheaterExitAsync(ct);
    Console.WriteLine($"극장 퇴장. 위변조 감지={tampered} (전투력={combatPower}, 골드={gold}, 엘리프={elleaf}, 마카롱={macaron})");
}

// ScenarioClient(자동 시나리오)는 8초면 충분했지만, 사람이 창 두 개를 오가며 손으로 누르는 이
// 콘솔 클라에선 그 사이 8초가 훌쩍 지나가버려 둘 다 타임아웃 나기 쉬움 — 수동 테스트에 맞게 늘림.
async Task MatchAsync()
{
    Console.WriteLine("매칭 신청... (최대 60초 대기 — 상대방 클라에서도 6번을 눌러야 성사됩니다)");
    var matchId = await session.MatchRequestAsync(TimeSpan.FromSeconds(60), ct);
    Console.WriteLine(matchId is long id ? $"매칭 성사! matchId={id}" : "상대를 찾지 못했습니다(타임아웃).");
}

async Task GuildMenuAsync()
{
    Console.WriteLine(" 1) 길드 생성   2) 길드 가입   3) 길드 탈퇴   4) 길드 정보 조회");
    Console.Write("> ");
    switch (Console.ReadLine())
    {
        case "1":
            Console.Write("길드 이름: ");
            string guildName = Console.ReadLine() ?? "";
            var created = await session.GuildCreateAsync(guildName, ct);
            if (created is long gid) { currentGuildId = gid; Console.WriteLine($"길드 생성 성공. guildId={gid}"); }
            else Console.WriteLine("길드 생성 실패(이름 중복 등)");
            break;
        case "2":
            Console.Write("가입할 길드 ID: ");
            if (long.TryParse(Console.ReadLine(), out long joinId))
            {
                bool joined = await session.GuildJoinAsync(joinId, ct);
                if (joined) currentGuildId = joinId;
                Console.WriteLine(joined ? "가입 성공" : "가입 실패");
            }
            break;
        case "3":
            if (currentGuildId is not long leaveId) { Console.WriteLine("소속된 길드가 없습니다."); break; }
            bool left = await session.GuildLeaveAsync(leaveId, ct);
            if (left) currentGuildId = null;
            Console.WriteLine(left ? "탈퇴 성공" : "탈퇴 실패");
            break;
        case "4":
            long? infoId = currentGuildId;
            if (infoId is null)
            {
                Console.Write("조회할 길드 ID: ");
                if (long.TryParse(Console.ReadLine(), out long id)) infoId = id;
            }
            if (infoId is not long gidToQuery) { Console.WriteLine("잘못된 입력입니다."); break; }

            var (success, guildInfoName, members) = await session.GuildInfoAsync(gidToQuery, ct);
            if (!success) { Console.WriteLine("조회 실패"); break; }
            Console.WriteLine($"길드명: {guildInfoName}, 멤버 수: {members.Count}");
            foreach (var (playerId, role) in members)
                Console.WriteLine($"  playerId={playerId} role={(role == 2 ? "길드장" : "멤버")}");
            break;
        default:
            Console.WriteLine("잘못된 선택입니다.");
            break;
    }
}
