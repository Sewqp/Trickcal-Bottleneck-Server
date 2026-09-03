namespace ChaosClient;

// DummyClient(--rampUp, 일정 속도로 늘어남)와 반대로, "편차가 큰 접속 주기"를 흉내내기 위한 옵션.
// 두 축을 동시에 불규칙하게 만듦: (1) 접속 자체가 일정 속도가 아니라 랜덤 휴지기 뒤 랜덤 크기의
// 버스트로 몰림 (2) 접속한 각 플레이어의 하트비트 간격도 고정이 아니라 매번 랜덤(플레이어별로 주기가
// 제각각) + 일부 비율은 하트비트 대신 짧은 간격으로 재접속을 반복(reconnect churn).
public sealed record ChaosOptions(
    string Host,
    int Port,
    int TotalCount,
    int BurstMin,
    int BurstMax,
    int IdleMinMs,
    int IdleMaxMs,
    int HeartbeatMinMs,
    int HeartbeatMaxMs,
    double ChurnRatio,
    int ChurnMinMs,
    int ChurnMaxMs,
    int DurationSeconds)
{
    public static ChaosOptions Parse(string[] args)
    {
        string host = "127.0.0.1";
        int port = 9100;
        int totalCount = 3000;
        int burstMin = 20;
        int burstMax = 400;
        int idleMinMs = 200;
        int idleMaxMs = 3000;
        int heartbeatMinMs = 3000;
        int heartbeatMaxMs = 20000;
        double churnRatio = 0.15; // 15%는 하트비트 대신 재접속을 반복
        int churnMinMs = 300;
        int churnMaxMs = 2000;
        int durationSeconds = 90;

        for (int i = 0; i < args.Length - 1; i += 2)
        {
            switch (args[i])
            {
                case "--host": host = args[i + 1]; break;
                case "--port": port = int.Parse(args[i + 1]); break;
                case "--count": totalCount = int.Parse(args[i + 1]); break;
                case "--burstMin": burstMin = int.Parse(args[i + 1]); break;
                case "--burstMax": burstMax = int.Parse(args[i + 1]); break;
                case "--idleMinMs": idleMinMs = int.Parse(args[i + 1]); break;
                case "--idleMaxMs": idleMaxMs = int.Parse(args[i + 1]); break;
                case "--heartbeatMinMs": heartbeatMinMs = int.Parse(args[i + 1]); break;
                case "--heartbeatMaxMs": heartbeatMaxMs = int.Parse(args[i + 1]); break;
                case "--churnRatio": churnRatio = double.Parse(args[i + 1]); break;
                case "--churnMinMs": churnMinMs = int.Parse(args[i + 1]); break;
                case "--churnMaxMs": churnMaxMs = int.Parse(args[i + 1]); break;
                case "--duration": durationSeconds = int.Parse(args[i + 1]); break;
            }
        }

        return new ChaosOptions(host, port, totalCount, burstMin, burstMax, idleMinMs, idleMaxMs,
            heartbeatMinMs, heartbeatMaxMs, churnRatio, churnMinMs, churnMaxMs, durationSeconds);
    }
}
