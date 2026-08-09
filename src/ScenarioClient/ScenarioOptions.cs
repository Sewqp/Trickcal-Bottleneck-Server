namespace ScenarioClient;

// DummyClient(접속/스트레스 전용)와 별개 — 스토리·강화·가챠·보드·길드 등 기능 시나리오를
// 여러 플레이어가 동시에(--players) 수행해보는 용도. story_duration은 design-plan-draft.md 3장에서
// 언급된 "실제 스토리 콘텐츠 대신 지정 시간 대기로 극장 재생을 흉내내는" 최소 기믹.
//
// PlayerNamePrefix는 --players > 1일 때 플레이어별로 겹치지 않게 "{prefix}_{index}_{guid}" 형태로
// 조합되는 접두사일 뿐(DummyPlayer의 이름 생성 방식과 동일). GuildName은 반대로 배치 전체에서
// 하나만 써서 전원이 같은 이름으로 동시에 길드 생성을 시도하게 함 — GuildRepository의 이름 중복
// 가드(unique 제약 + 23505 캐치)가 실제 동시 요청 하에서 정확히 1명만 성공시키는지 검증하기 위함.
public sealed record ScenarioOptions(
    string Host,
    int Port,
    string PlayerNamePrefix,
    int StoryDurationSeconds,
    string GuildName,
    int PlayerCount,
    int RampUpPerSecond)
{
    public static ScenarioOptions Parse(string[] args)
    {
        string host = "127.0.0.1";
        int port = 9100;
        string playerNamePrefix = "scenario";
        int storyDurationSeconds = 5;
        string guildName = $"guild_{Guid.NewGuid():N}"[..16];
        int playerCount = 1;
        int rampUpPerSecond = 0; // 0이면 --players 값 그대로(한꺼번에 스폰)

        for (int i = 0; i < args.Length - 1; i += 2)
        {
            switch (args[i])
            {
                case "--host": host = args[i + 1]; break;
                case "--port": port = int.Parse(args[i + 1]); break;
                case "--name": playerNamePrefix = args[i + 1]; break;
                case "--storyDuration": storyDurationSeconds = int.Parse(args[i + 1]); break;
                case "--guildName": guildName = args[i + 1]; break;
                case "--players": playerCount = int.Parse(args[i + 1]); break;
                case "--rampUp": rampUpPerSecond = int.Parse(args[i + 1]); break;
            }
        }

        if (rampUpPerSecond <= 0) rampUpPerSecond = playerCount;
        return new ScenarioOptions(host, port, playerNamePrefix, storyDurationSeconds, guildName, playerCount, rampUpPerSecond);
    }
}
