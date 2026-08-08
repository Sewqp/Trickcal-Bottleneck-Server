using Npgsql;

namespace GameServer.DB;

public sealed class DbConnectionPool
{
    public static readonly DbConnectionPool Instance = new();

    private string _connectionString = "";

    private DbConnectionPool() { }

    public void Init(string connectionString) => _connectionString = connectionString;

    // Npgsql이 내부적으로 커넥션 풀을 관리하므로 Open/Close 패턴을 그대로 사용
    public NpgsqlConnection GetConnection() => new(_connectionString);
}
