using System.Net;
using System.Net.Sockets;
using System.Text;
using GameServer.DB;
using GameServer.DB.Repository;
using GameServer.Grains;
using GameServer.Network;
using GameServer.Packet;
using GameServer.Packet.Handler;
using GameServer.Progress;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Orleans;
using StackExchange.Redis;

namespace GameServer.Checks;

internal static class IntegrationChecks
{
    private static IGrainFactory _factory = null!;
    private static string _postgres = "";
    private static IDatabase Redis => RedisClient.Instance.Db;

    public static async Task RunAsync()
    {
        _postgres = Environment.GetEnvironmentVariable("TRICKCAL_TEST_POSTGRES")
            ?? throw new InvalidOperationException("TRICKCAL_TEST_POSTGRES must name an empty, disposable database.");
        string redis = Environment.GetEnvironmentVariable("TRICKCAL_TEST_REDIS")
            ?? throw new InvalidOperationException("TRICKCAL_TEST_REDIS must name an empty, disposable Redis database.");
        var options = new NpgsqlConnectionStringBuilder(_postgres);
        Check.That(options.Database?.StartsWith("trickcal_checks_", StringComparison.Ordinal) == true,
            "test database must have trickcal_checks_ prefix");
        Environment.SetEnvironmentVariable("TRICKCAL_ENABLE_FAULT_INJECTION", "1");
        Environment.SetEnvironmentVariable("TRICKCAL_ENABLE_DEBUG_COMMANDS", "0");
        DbConnectionPool.Instance.Init(_postgres);
        RedisClient.Instance.Init(redis);
        var endpoint = RedisClient.Instance.Multiplexer.GetEndPoints().Single();
        Check.That(await RedisClient.Instance.Multiplexer.GetServer(endpoint).DatabaseSizeAsync(Redis.Database) == 0,
            "refuse nonempty Redis test database");
        Check.That(await ScalarAsync<long>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='public'") == 0,
            "refuse nonempty PostgreSQL test database");
        await SqlAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "DB/schema.sql")));
        await SqlAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "DB/seed_characters.sql")));

        using var host = Host.CreateDefaultBuilder().ConfigureLogging(log => log.ClearProviders().AddSimpleConsole()
                .SetMinimumLevel(LogLevel.Error))
            .UseOrleans(silo => silo.UseLocalhostClustering(FreePort(), FreePort(),
                serviceId: "progress-checks", clusterId: Guid.NewGuid().ToString("N"))).Build();
        await host.StartAsync();
        _factory = host.Services.GetRequiredService<IGrainFactory>();
        OrleansClient.Instance.Init(_factory);
        try
        {
            await GrowthAsync();
            await GachaAsync();
            await TransactionAsync();
            await SessionsAsync();
            await MatchingAsync();
            await NetworkAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRICKCAL_GACHA_FAULT", null);
            await host.StopAsync();
            await RedisClient.Instance.Multiplexer.CloseAsync();
        }
    }

    private static async Task GrowthAsync()
    {
        var player = await NewPlayerAsync([1, 2]);
        var (grain, session) = await AttachAsync(player);
        var duplicate = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ =>
            grain.ExecuteProgressAsync(session, (ushort)PacketId.BoardUnlockRequest, Board(1, 1))));
        Check.That(duplicate.Count(Success) == 1, "24 concurrent duplicate unlocks charge exactly once");
        Check.That(await MoneyAsync(player, "gold") == 499900, "one board cost");
        Check.That(!Success(await CommandAsync(grain, session, PacketId.BoardUnlockRequest, Board(3, 1))), "unowned board rejected");
        Check.That(!Success(await CommandAsync(grain, session, PacketId.CharacterEnhanceExitRequest, Integers(3, 2))), "unowned enhancement rejected");
        Check.That(!Success(await CommandAsync(grain, session, PacketId.CharacterEnhanceExitRequest, Integers(1, int.MaxValue))), "oversized target rejected");
        Check.That(!Success(await CommandAsync(grain, session, PacketId.BoardUnlockRequest, [1, 2])), "malformed mutation rejected");

        await Redis.HashSetAsync(CurrencyRepository.GetRedisKey(player), "gold", 0);
        long macaron = await MoneyAsync(player, "macaron");
        Check.That(!Success(await CommandAsync(grain, session, PacketId.CharacterEnhanceExitRequest, Integers(1, 3))), "insufficient gold rejected");
        Check.That(await MoneyAsync(player, "macaron") == macaron, "no partial macaron debit");
        await Redis.HashSetAsync(CurrencyRepository.GetRedisKey(player), "gold", 500000);
        for (int level = 2; level <= 30; level++)
            Check.That(Success(await CommandAsync(grain, session, PacketId.CharacterEnhanceExitRequest, Integers(1, level))), "incremental enhancement succeeds");
        Check.That(Success(await CommandAsync(grain, session, PacketId.CharacterEnhanceExitRequest, Integers(2, 30))), "batch enhancement succeeds");
        Check.That(Success(await CommandAsync(grain, session, PacketId.BoardUnlockRequest, Board(1, 1, true))), "global board unlock");
        long before = await PowerAsync(player);
        var loaded = await ProgressRepository.LoadAsync(player);
        Check.That(before == loaded.CalculateTotal(), "incremental power equals full recalculation");

        Check.That(Success(await CommandAsync(grain, session, PacketId.TheaterEnterRequest, [])), "theater enter");
        Check.That((await CommandAsync(grain, session, PacketId.CharacterEnhanceExitRequest, Integers(1, 31))).Length == 0,
            "mutation blocked inside theater actor");
        long snapshotGold = await MoneyAsync(player, "gold");
        await Redis.HashIncrementAsync(CurrencyRepository.GetRedisKey(player), "gold", 99);
        byte[] exit = await CommandAsync(grain, session, PacketId.TheaterExitRequest, []);
        Check.That(exit[4] == 1 && await MoneyAsync(player, "gold") == snapshotGold, "theater detects and restores tampered money");
        Check.That((await CommandAsync(grain, session, PacketId.DebugGrantCurrencyRequest, [])).Length == 0, "debug grant disabled by default");
        await grain.OnDisconnectAsync(session);
        Console.WriteLine("PASS: concurrent growth, ownership, atomic wallet, rounding, theater");
    }

    private static async Task GachaAsync()
    {
        long player = await NewPlayerAsync([1]);
        var (grain, session) = await AttachAsync(player);
        Guid id = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            CommandAsync(grain, session, PacketId.GachaRequest, id.ToByteArray())));
        Check.That(results.All(r => r.SequenceEqual(results[0])) && Success(results[0]), "concurrent gacha request IDs replay same draw");
        int compensation = BitConverter.ToUInt16(results[0], 5);
        Check.That(await MoneyAsync(player, "elleaf") == 4000 + compensation, "gacha charged and compensated once");
        Check.That((await CommandAsync(grain, session, PacketId.GachaRequest, [1])).Length == 5, "malformed gacha rejected");

        foreach (string fault in new[] { "after-reserve", "after-sql" })
        {
            long recovering = await NewPlayerAsync([]);
            var (recoverGrain, oldSession) = await AttachAsync(recovering);
            var request = Guid.NewGuid();
            Environment.SetEnvironmentVariable("TRICKCAL_GACHA_FAULT", fault);
            try
            {
                await Check.ThrowsAsync(() => CommandAsync(recoverGrain, oldSession, PacketId.GachaRequest, request.ToByteArray()), fault);
                Check.That(await Redis.KeyExistsAsync(ProgressRepository.PendingKey(recovering)), "failed gacha keeps recovery journal");
                Check.That(await MoneyAsync(recovering, "elleaf") == 4000, "reserve debits exactly once");
            }
            finally { Environment.SetEnvironmentVariable("TRICKCAL_GACHA_FAULT", null); }
            var (_, replacement) = await AttachAsync(recovering);
            byte[] recovered = await CommandAsync(recoverGrain, replacement, PacketId.GachaRequest, request.ToByteArray());
            byte[] replay = await CommandAsync(recoverGrain, replacement, PacketId.GachaRequest, request.ToByteArray());
            Check.That(Success(recovered) && recovered.SequenceEqual(replay), "journal recovers exact result");
            int refund = BitConverter.ToUInt16(recovered, 5);
            Check.That(await MoneyAsync(recovering, "elleaf") == 4000 + refund, "recovery does not double debit/credit");
            Check.That(!await Redis.KeyExistsAsync(ProgressRepository.PendingKey(recovering)), "completed journal cleared");
            long observedPower = await PowerAsync(recovering);
            var state = await ProgressRepository.LoadAsync(recovering);
            Check.That(observedPower == state.CalculateTotal(), "recovered power matches owned characters");
            await recoverGrain.OnDisconnectAsync(oldSession);
            Check.That(await recoverGrain.IsCurrentSessionAsync(replacement), "old disconnect preserves recovered session");
            await recoverGrain.OnDisconnectAsync(replacement);
        }
        await grain.OnDisconnectAsync(session);
        Console.WriteLine("PASS: gacha request replay and injected reserve/SQL failure recovery");
    }

    private static async Task TransactionAsync()
    {
        long player = await NewPlayerAsync([1]);
        var (grain, session) = await AttachAsync(player);
        await CommandAsync(grain, session, PacketId.BoardUnlockRequest, Board(1, 1));
        await Redis.HashSetAsync($"player:cboard:{player}", "999999:1", 1);
        await Check.ThrowsAsync(() => grain.FlushAsync(), "foreign-key failure during snapshot save");
        Check.That(await ScalarAsync<long>($"SELECT gold FROM player_currency WHERE player_id={player}") == 500000,
            "failed later statement rolls back earlier currency write");
        Check.That(await Redis.SetContainsAsync(ProgressRepository.DirtyPlayers, player), "failed flush keeps dirty retry marker");
        await Redis.HashDeleteAsync($"player:cboard:{player}", "999999:1");
        using (var retryStop = new CancellationTokenSource())
        {
            var retry = new ProgressFlushWorker(retryStop.Token).RunAsync();
            for (int attempt = 0; attempt < 100 && await Redis.SetContainsAsync(ProgressRepository.DirtyPlayers, player); attempt++)
                await Task.Delay(20);
            retryStop.Cancel();
            await retry;
        }
        Check.That(await ScalarAsync<long>($"SELECT gold FROM player_currency WHERE player_id={player}") == 499900, "retry commits consistent snapshot");
        Check.That(!await Redis.SetContainsAsync(ProgressRepository.DirtyPlayers, player), "successful commit clears dirty marker");
        await grain.OnDisconnectAsync(session);
        Console.WriteLine("PASS: SQL rollback and flush retry");
    }

    private static async Task SessionsAsync()
    {
        long player = await NewPlayerAsync([1]);
        using var old = await SocketPair.OpenAsync();
        using var current = await SocketPair.OpenAsync();
        await old.Session.AttachPlayerAsync(player);
        await current.Session.AttachPlayerAsync(player);
        var grain = _factory.GetGrain<IPlayerGrain>(player);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => old.Session.DisconnectAsync()));
        Check.That(await grain.IsCurrentSessionAsync(current.Session.SessionId), "repeated stale disconnect leaves new grain owner");
        Check.That(SessionManager.Instance.GetByPlayerId(player) == current.Session, "stale disconnect leaves new local index");
        Check.That((await CommandAsync(grain, old.Session.SessionId, PacketId.GachaRequest, [])).Length == 0, "stale mutation rejected in actor");

        await CommandAsync(grain, current.Session.SessionId, PacketId.BoardUnlockRequest, Board(1, 1));
        DbConnectionPool.Instance.Init("Host=127.0.0.1;Port=1;Database=unavailable;Username=postgres;Timeout=1");
        try { await current.Session.DisconnectAsync(); }
        finally { DbConnectionPool.Instance.Init(_postgres); }
        Check.That(SessionManager.Instance.GetByPlayerId(player) is null, "database failure does not leak local index");
        Check.That(!await grain.IsCurrentSessionAsync(current.Session.SessionId), "database failure still clears grain owner");
        Check.That(await Redis.SetContainsAsync(ProgressRepository.DirtyPlayers, player), "database outage retains dirty state");
        var buffer = new byte[1];
        Check.That(await current.Peer.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) == 0,
            "database outage still closes socket");
        await grain.FlushAsync();
        Console.WriteLine("PASS: repeated disconnect, replaced session, unavailable DB cleanup");
    }

    private static async Task MatchingAsync()
    {
        long first = await NewPlayerAsync([]), second = await NewPlayerAsync([]);
        var firstGrain = _factory.GetGrain<IPlayerGrain>(first);
        var secondGrain = _factory.GetGrain<IPlayerGrain>(second);
        var firstObserver = new Observer();
        var secondObserver = new Observer();
        Guid firstSession = Guid.NewGuid(), secondSession = Guid.NewGuid();
        await firstGrain.SubscribeAsync(firstSession, _factory.CreateObjectReference<IPlayerSessionObserver>(firstObserver));
        await secondGrain.SubscribeAsync(secondSession, _factory.CreateObjectReference<IPlayerSessionObserver>(secondObserver));
        var matcher = _factory.GetGrain<IMatchGrain>(0);
        const string firstCause = "00-11111111111111111111111111111111-1111111111111111-01";
        const string secondCause = "00-22222222222222222222222222222222-2222222222222222-01";
        Check.That(await matcher.RequestMatchAsync(first, Guid.NewGuid(), firstCause) == MatchRequestStatus.StaleSession,
            "stale match admission is rejected");
        Check.That(await matcher.RequestMatchAsync(first, firstSession, firstCause) == MatchRequestStatus.Queued,
            "first match request reports queued");
        Check.That(await matcher.RequestMatchAsync(first, firstSession, secondCause) == MatchRequestStatus.AlreadyQueued,
            "duplicate match reports already queued");
        await matcher.CancelMatchAsync(first, Guid.NewGuid());
        Check.That(await matcher.RequestMatchAsync(second, secondSession, secondCause) == MatchRequestStatus.Created,
            "second player creates match despite stale cancellation");
        for (int attempt = 0; attempt < 100 && (firstObserver.Matches.IsEmpty || secondObserver.Matches.IsEmpty); attempt++)
            await Task.Delay(10);
        Check.That(firstObserver.Matches.TryDequeue(out var firstResult), "first player receives notification");
        Check.That(secondObserver.Matches.TryDequeue(out var secondResult), "second player receives notification");
        Check.That(firstResult.TraceParent == firstCause && secondResult.TraceParent == secondCause,
            "each notification retains its own original request, not duplicate request or peer cause");
        Check.That(firstResult.Packet.SequenceEqual(secondResult.Packet) && Success(firstResult.Packet),
            "both players receive identical successful match");
        Check.That(!await firstGrain.SendMatchResultAsync(Guid.NewGuid(), firstResult.Packet, firstCause),
            "notification cannot be forwarded to a replaced session");
        Check.That(await matcher.RequestMatchAsync(first, firstSession, firstCause) == MatchRequestStatus.Queued,
            "completed entry was removed");
        await matcher.CancelMatchAsync(first, firstSession);
        Check.That(await matcher.RequestMatchAsync(second, secondSession, secondCause) == MatchRequestStatus.Queued,
            "cancelled player is no longer a candidate");
        await matcher.CancelMatchAsync(second, secondSession);
        await firstGrain.OnDisconnectAsync(firstSession);
        await secondGrain.OnDisconnectAsync(secondSession);
        Console.WriteLine("PASS: match admission, duplicates, cancellation, session fencing, per-player causal context");
    }

    private static async Task NetworkAsync()
    {
        var dispatcher = PacketDispatcher.Instance;
        dispatcher.Register(PacketId.LoginRequest, LoginHandler.HandleAsync);
        dispatcher.Register(PacketId.ReconnectRequest, ReconnectHandler.HandleAsync);
        dispatcher.Register(PacketId.GachaRequest, GachaHandler.HandleAsync);
        dispatcher.Register(PacketId.Heartbeat, HeartbeatHandler.HandleAsync);
        dispatcher.Register(PacketId.DebugGrantCurrencyRequest, DebugHandler.HandleGrantCurrencyAsync);
        using var cancellation = new CancellationTokenSource();
        int port = FreePort();
        var serverTask = new TcpServer(port, cancellation.Token).StartAsync();
        try
        {
            using var first = new TcpClient();
            await first.ConnectAsync(IPAddress.Loopback, port);
            byte[] name = Encoding.UTF8.GetBytes("wire_" + Guid.NewGuid().ToString("N")[..12]);
            byte[] loginPayload = new byte[2 + name.Length];
            BitConverter.TryWriteBytes(loginPayload.AsSpan(), (ushort)name.Length); name.CopyTo(loginPayload, 2);
            byte[] login = await ExchangeAsync(first, PacketId.LoginRequest, loginPayload);
            Check.That(Success(login) && login.Length == 49, "TCP login keeps existing packet format");
            Check.That(!Success(await ExchangeAsync(first, PacketId.DebugGrantCurrencyRequest, [])), "disabled debug returns failure without hanging client");
            long player = BitConverter.ToInt64(login, 5);
            Check.That(await MoneyAsync(player, "elleaf") == 1000, "starter wallet persisted during account creation");
            string token = Encoding.UTF8.GetString(login, 13, 36);
            using var second = new TcpClient(); await second.ConnectAsync(IPAddress.Loopback, port);
            byte[] reconnect = await ExchangeAsync(second, PacketId.ReconnectRequest, Encoding.UTF8.GetBytes(token));
            Check.That(Success(reconnect) && reconnect.Length == 41, "reconnect returns replacement token");
            string replacement = Encoding.UTF8.GetString(reconnect, 5, 36);
            using var rejected = new TcpClient(); await rejected.ConnectAsync(IPAddress.Loopback, port);
            Check.That(!Success(await ExchangeAsync(rejected, PacketId.ReconnectRequest, Encoding.UTF8.GetBytes(token))), "consumed token cannot replay");
            using var third = new TcpClient(); await third.ConnectAsync(IPAddress.Loopback, port);
            Check.That(Success(await ExchangeAsync(third, PacketId.ReconnectRequest, Encoding.UTF8.GetBytes(replacement))), "reconnect can be repeated with renewed token");
            var request = Guid.NewGuid().ToByteArray();
            byte[] result = await ExchangeAsync(third, PacketId.GachaRequest, request);
            Check.That(Success(result), "TCP gacha succeeds");
            byte[] repeated = await ExchangeAsync(third, PacketId.GachaRequest, request);
            Check.That(result.SequenceEqual(repeated), "TCP same request returns exact receipt");
            Check.That((await ExchangeAsync(third, PacketId.GachaRequest, [1])).Length == 5, "TCP malformed gacha explicitly fails");
        }
        finally
        {
            cancellation.Cancel(); await serverTask;
            await Task.WhenAll(SessionManager.Instance.GetAll().Select(s => s.DisconnectAsync()));
        }
        Console.WriteLine("PASS: TCP login, renewable one-use reconnect token, gacha replay");
    }

    private static async Task<(IPlayerGrain, Guid)> AttachAsync(long player)
    {
        var grain = _factory.GetGrain<IPlayerGrain>(player);
        var observer = _factory.CreateObjectReference<IPlayerSessionObserver>(new Observer());
        var session = Guid.NewGuid();
        await grain.SubscribeAsync(session, observer);
        return (grain, session);
    }

    private static async Task<long> NewPlayerAsync(int[] owned)
    {
        var (player, _) = await PlayerRepository.Instance.GetOrCreateByNameAsync("check_" + Guid.NewGuid().ToString("N")[..12]);
        await SqlAsync($"UPDATE player_currency SET gold=500000,macaron=500000,elleaf=5000 WHERE player_id={player}");
        await CharacterRepository.Instance.AcquireCharactersAsync(player, owned);
        return player;
    }

    private static Task<byte[]> CommandAsync(IPlayerGrain grain, Guid session, PacketId id, byte[] payload) =>
        grain.ExecuteProgressAsync(session, (ushort)id, payload);
    private static bool Success(byte[] packet) => packet.Length > 4 && packet[4] == 1;
    private static async Task<long> MoneyAsync(long player, string field) =>
        (long)await Redis.HashGetAsync(CurrencyRepository.GetRedisKey(player), field);
    private static async Task<long> PowerAsync(long player) => (long)await Redis.StringGetAsync(PlayerStatRepository.GetRedisKey(player));
    private static byte[] Integers(int a, int b)
    {
        byte[] data = new byte[8]; BitConverter.TryWriteBytes(data.AsSpan(), a); BitConverter.TryWriteBytes(data.AsSpan(4), b); return data;
    }
    private static byte[] Board(int character, int number, bool global = false) => [..Integers(character, number), (byte)(global ? 1 : 0)];
    private static async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync();
    }
    private static async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); return (T)(await command.ExecuteScalarAsync())!;
    }
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private static async Task<byte[]> ExchangeAsync(TcpClient client, PacketId id, byte[] payload)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stream = client.GetStream();
        await stream.WriteAsync(PacketWriter.Build(id, payload), timeout.Token);
        var header = new byte[4]; await stream.ReadExactlyAsync(header, timeout.Token);
        int size = BitConverter.ToUInt16(header); var response = new byte[size]; header.CopyTo(response, 0);
        await stream.ReadExactlyAsync(response.AsMemory(4), timeout.Token); return response;
    }

    private sealed class Observer : IPlayerSessionObserver
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<(byte[] Packet, string? TraceParent)> Matches = new();
        public Task DeliverAsync(byte[] packet) => Task.CompletedTask;
        public Task DeliverMatchResultAsync(byte[] packet, string? requestTraceParent)
        {
            Matches.Enqueue((packet, requestTraceParent));
            return Task.CompletedTask;
        }
    }
    private sealed class SocketPair(TcpClient peer, ClientSession session) : IDisposable
    {
        public TcpClient Peer { get; } = peer;
        public ClientSession Session { get; } = session;
        public static async Task<SocketPair> OpenAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var peer = new TcpClient(); await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            var server = await listener.AcceptTcpClientAsync(); listener.Stop();
            var session = new ClientSession(server, CancellationToken.None);
            SessionManager.Instance.Add(session);
            return new(peer, session);
        }
        public void Dispose() => Peer.Dispose();
    }
}
