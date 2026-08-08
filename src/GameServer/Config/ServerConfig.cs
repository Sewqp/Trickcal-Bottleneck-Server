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
        // Maximum Pool Size=100 — docker-compose postgres:16이 아직 max_connections 튜닝 전(기본값 100)이라
        // 앱 풀 크기가 서버 한도를 넘지 않도록 맞춤 (Game-Server-CSharp에서 겪은 max_connections 초과 문제 재현 방지)
        PostgresConnectionString = Env("POSTGRES_CONN")
            ?? "Host=127.0.0.1;Port=5432;Database=trickcal_bottleneck;Username=postgres;Password=password;" +
               "Pooling=true;Minimum Pool Size=5;Maximum Pool Size=100;",
        RedisConnectionString = Env("REDIS_CONN") ?? "127.0.0.1:6379",
        OrleansClusterId = Env("ORLEANS_CLUSTER_ID") ?? "trickcal-bottleneck-cluster",
        OrleansServiceId = Env("ORLEANS_SERVICE_ID") ?? "TrickcalBottleneckServer",
        OrleansSiloPort = int.TryParse(Env("ORLEANS_SILO_PORT"), out var sp) ? sp : 11111,
        OrleansGatewayPort = int.TryParse(Env("ORLEANS_GATEWAY_PORT"), out var gp) ? gp : 30000,
    };

    private static string? Env(string key) => Environment.GetEnvironmentVariable(key);
}
