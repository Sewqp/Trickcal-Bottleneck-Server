namespace GameServer.Config;

public sealed class ServerConfig
{
    public static readonly ServerConfig Instance = Load();

    public int TcpPort { get; init; }
    public string PostgresConnectionString { get; init; } = "";
    public string RedisConnectionString { get; init; } = "";
    public string OrleansClusterId { get; init; } = "";
    public string OrleansServiceId { get; init; } = "";
    public int OrleansSiloPort { get; init; }
    public int OrleansGatewayPort { get; init; }

    private static ServerConfig Load() => new()
    {
        TcpPort = int.TryParse(Env("TCP_PORT"), out var p) ? p : 9100,
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
    };

    private static string? Env(string key) => Environment.GetEnvironmentVariable(key);
}
