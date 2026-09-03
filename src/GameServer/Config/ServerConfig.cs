namespace GameServer.Config;

public sealed class ServerConfig
{
    public static readonly ServerConfig Instance = Load();

    public int TcpPort { get; init; }
    public int TcpBacklog { get; init; }
    public int TcpAcceptDelayMs { get; init; }
    public string PostgresConnectionString { get; init; } = "";
    public string RedisConnectionString { get; init; } = "";
    public string OrleansClusterId { get; init; } = "";
    public string OrleansServiceId { get; init; } = "";
    public int OrleansSiloPort { get; init; }
    public int OrleansGatewayPort { get; init; }
    public string OtlpEndpoint { get; init; } = "";
    public string ApmCollectorEndpoint { get; init; } = "";
    public string WatchPlayerName { get; init; } = "";
    public int DashboardPort { get; init; }

    private static ServerConfig Load() => new()
    {
        TcpPort = int.TryParse(Env("TCP_PORT"), out var p) ? p : 9100,
        // 데모용 병목 재현 — accept backlog를 낮춰서 대량 동접 순간에 접속 실패 스파이크를 일부러
        // 만들어보기 위함(기본값 512는 Windows/Linux 흔한 관행값, 원래 코드에 하드코딩돼 있던 값 그대로).
        TcpBacklog = int.TryParse(Env("TCP_BACKLOG"), out var bl) ? bl : 512,
        // 데모용 — accept 루프를 인위적으로 느리게 만들어서 낮은 backlog가 loopback에서도 실제로
        // 접속 실패를 유발하게 함(로컬호스트는 accept 자체가 순식간이라 backlog만 낮춰선 안 밀림).
        TcpAcceptDelayMs = int.TryParse(Env("TCP_ACCEPT_DELAY_MS"), out var ad) ? ad : 0,
        // Maximum Pool Size=120 — docker-compose postgres:16의 max_connections를 300으로 튜닝해둠
        // (Game-Server-CSharp 3만 명 동접 검증 때 겪은 max_connections 초과 문제 재현 방지, 같은 조합 재사용)
        //
        // 환경변수명을 POSTGRES_CONN/REDIS_CONN이 아니라 TRICKCAL_ 접두사로 분리한 이유: 이 머신에
        // Game-Server-CSharp가 이미 POSTGRES_CONN 사용자 환경변수를 설정해뒀는데 이름이 같으면 그 값을
        // 그대로 물려받아 엉뚱한 DB(game_server_cs)에 붙어버림(2026-08-10 실제로 겪음). 포트도 5432/6379
        // 대신 5433/6380 사용 — 이 머신의 네이티브 PostgreSQL/Redis Windows 서비스가 5432/6379를 이미
        // 점유 중이라 겹치면 docker-compose 쪽이 아니라 그 네이티브 서비스로 잘못 연결됨.
        PostgresConnectionString = Env("TRICKCAL_POSTGRES_CONN")
            ?? "Host=127.0.0.1;Port=5433;Database=trickcal_bottleneck;Username=postgres;Password=password;" +
               "Pooling=true;Minimum Pool Size=5;Maximum Pool Size=120;",
        RedisConnectionString = Env("TRICKCAL_REDIS_CONN") ?? "127.0.0.1:6380",
        OrleansClusterId = Env("ORLEANS_CLUSTER_ID") ?? "trickcal-bottleneck-cluster",
        OrleansServiceId = Env("ORLEANS_SERVICE_ID") ?? "TrickcalBottleneckServer",
        OrleansSiloPort = int.TryParse(Env("ORLEANS_SILO_PORT"), out var sp) ? sp : 11111,
        OrleansGatewayPort = int.TryParse(Env("ORLEANS_GATEWAY_PORT"), out var gp) ? gp : 30000,
        // docker-compose.yml의 apm-server가 호스트 8200 포트로 OTLP gRPC/HTTP를 같이 서비스함
        // (Elastic 공식 문서 확인: 별도 플래그 없이 기본 설정으로 동작).
        OtlpEndpoint = Env("OTEL_EXPORTER_OTLP_ENDPOINT") ?? "http://127.0.0.1:8200",
        // LLM 기반 로컬 APM 프로젝트(별도 저장소)의 CollectionServer — 패킷 처리 메트릭을 여기로 전송해
        // 임계값 이상탐지 + 로컬 LLM(LM Studio) 원인분석 파이프라인에 태운다.
        ApmCollectorEndpoint = Env("APM_COLLECTOR_ENDPOINT") ?? "http://127.0.0.1:5219",
        // 데모/영상 녹화용 — 이 이름으로 로그인한 플레이어 딱 한 명만 PlayerWatcher가 콘솔에 자세히
        // 찍음(패킷/화면 전환 + 엘리프 소비). 전원에게 이걸 하면 로그가 병목이 되지만 한 명만이면 무해함.
        WatchPlayerName = Env("WATCH_PLAYER_NAME") ?? "admin",
        // Orleans Dashboard(실로/그레인 실시간 모니터링) — 실로를 여러 개 띄워서 실험할 때, 인스턴스마다
        // TCP_PORT/ORLEANS_SILO_PORT/ORLEANS_GATEWAY_PORT와 같이 이 포트도 따로 지정해야 겹치지 않음.
        // 어느 인스턴스의 대시보드를 열어도 같은 Redis 클러스터링을 공유하는 실로는 전부 한 화면에 보임.
        DashboardPort = int.TryParse(Env("DASHBOARD_PORT"), out var dp) ? dp : 8888,
    };

    private static string? Env(string key) => Environment.GetEnvironmentVariable(key);
}
