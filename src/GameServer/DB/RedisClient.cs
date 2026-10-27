using StackExchange.Redis;

namespace GameServer.DB;

public sealed class RedisClient
{
    public static readonly RedisClient Instance = new();

    private ConnectionMultiplexer? _multiplexer;
    private IDatabase? _db;

    private RedisClient() { }

    public void Init(string connectionString)
    {
        _multiplexer = ConnectionMultiplexer.Connect(connectionString);
        _db = _multiplexer.GetDatabase();
    }

    public IDatabase Db => _db ?? throw new InvalidOperationException("RedisClient is not initialized.");

    public bool IsConnected => _multiplexer?.IsConnected ?? false;

    // OTel StackExchangeRedis 계측(AddRedisInstrumentation)이 span을 걸려면 이 인스턴스가 필요함
    public ConnectionMultiplexer Multiplexer => _multiplexer ?? throw new InvalidOperationException("RedisClient is not initialized.");
}
