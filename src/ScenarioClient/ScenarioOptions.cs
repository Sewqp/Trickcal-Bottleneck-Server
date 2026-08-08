namespace ScenarioClient;

// DummyClient(접속/스트레스 전용)와 별개 — 스토리·강화·가챠·보드·길드 등 기능 시나리오를
// 한 플레이어가 순서대로 수행해보는 용도. story_duration은 design-plan-draft.md 3장에서
// 언급된 "실제 스토리 콘텐츠 대신 지정 시간 대기로 극장 재생을 흉내내는" 최소 기믹.
public sealed record ScenarioOptions(
    string Host,
    int Port,
    string PlayerName,
    int StoryDurationSeconds,
    string GuildName)
{
    public static ScenarioOptions Parse(string[] args)
    {
        string host = "127.0.0.1";
        int port = 9100;
        string playerName = $"scenario_{Guid.NewGuid():N}"[..20];
        int storyDurationSeconds = 5;
        string guildName = $"guild_{Guid.NewGuid():N}"[..16];

        for (int i = 0; i < args.Length - 1; i += 2)
        {
            switch (args[i])
            {
                case "--host": host = args[i + 1]; break;
                case "--port": port = int.Parse(args[i + 1]); break;
                case "--name": playerName = args[i + 1]; break;
                case "--storyDuration": storyDurationSeconds = int.Parse(args[i + 1]); break;
                case "--guildName": guildName = args[i + 1]; break;
            }
        }

        return new ScenarioOptions(host, port, playerName, storyDurationSeconds, guildName);
    }
}
