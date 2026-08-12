using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using StackExchange.Redis;
using GameServer.Config;
using GameServer.DB;
using GameServer.Grains;
using GameServer.Logging;
using GameServer.Network;
using GameServer.Packet;
using GameServer.Packet.Handler;

var config = ServerConfig.Instance;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

AsyncLogger.Instance.Init(cts.Token);

DbConnectionPool.Instance.Init(config.PostgresConnectionString);
Console.WriteLine("[DB] PostgreSQL connection pool ready.");

RedisClient.Instance.Init(config.RedisConnectionString);
Console.WriteLine("[DB] Redis connected.");

var hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.UseOrleans(silo =>
{
    silo.Configure<ClusterOptions>(o =>
    {
        o.ClusterId = config.OrleansClusterId;
        o.ServiceId = config.OrleansServiceId;
    });
    silo.UseRedisClustering(o =>
    {
        o.ConfigurationOptions = ConfigurationOptions.Parse(config.RedisConnectionString);
        o.ConfigurationOptions.AbortOnConnectFail = false;
    });
    silo.ConfigureEndpoints(siloPort: config.OrleansSiloPort, gatewayPort: config.OrleansGatewayPort);
});

using var host = hostBuilder.Build();
await host.StartAsync();
OrleansClient.Instance.Init(host.Services.GetRequiredService<IGrainFactory>());
Console.WriteLine($"[Orleans] Silo active. Cluster={config.OrleansClusterId}");

_ = new HeartbeatManager(cts.Token).RunAsync();

var dispatcher = PacketDispatcher.Instance;
dispatcher.Register(PacketId.LoginRequest,       LoginHandler.HandleAsync);
dispatcher.Register(PacketId.Heartbeat,          HeartbeatHandler.HandleAsync);
dispatcher.Register(PacketId.ReconnectRequest,   ReconnectHandler.HandleAsync);
dispatcher.Register(PacketId.MatchRequest,       MatchHandler.HandleAsync);
dispatcher.Register(PacketId.ItemAcquireRequest, ItemHandler.HandleAcquireAsync);
dispatcher.Register(PacketId.InventoryRequest,   ItemHandler.HandleInventoryRequestAsync);
dispatcher.Register(PacketId.ItemUseRequest,     ItemHandler.HandleUseAsync);

dispatcher.Register(PacketId.TheaterEnterRequest, TheaterHandler.HandleEnterAsync);
dispatcher.Register(PacketId.TheaterExitRequest,  TheaterHandler.HandleExitAsync);
dispatcher.Register(PacketId.TheaterTouchRequest, TheaterHandler.HandleTouchAsync);

dispatcher.Register(PacketId.CharacterListRequest,         CharacterHandler.HandleListRequestAsync);
dispatcher.Register(PacketId.CharacterEnhanceEnterRequest, CharacterHandler.HandleEnhanceEnterAsync);
dispatcher.Register(PacketId.CharacterEnhanceExitRequest,  CharacterHandler.HandleEnhanceExitAsync);

dispatcher.Register(PacketId.BoardListRequest,   BoardHandler.HandleListRequestAsync);
dispatcher.Register(PacketId.BoardUnlockRequest, BoardHandler.HandleUnlockRequestAsync);

dispatcher.Register(PacketId.GachaRequest, GachaHandler.HandleAsync);

dispatcher.Register(PacketId.GuildCreateRequest, GuildHandler.HandleCreateAsync);
dispatcher.Register(PacketId.GuildJoinRequest,   GuildHandler.HandleJoinAsync);
dispatcher.Register(PacketId.GuildLeaveRequest,  GuildHandler.HandleLeaveAsync);
dispatcher.Register(PacketId.GuildInfoRequest,   GuildHandler.HandleInfoRequestAsync);

var server = new TcpServer(config.TcpPort, cts.Token);
await server.StartAsync();

// 서버 강제 종료(design-plan-draft.md 5절 트리거 ④) — 접속 중이던 세션 전부를 순회하며
// SessionFlush.FlushOneAsync를 반복 호출(ClientSession.DisconnectAsync 안에서 호출됨)
foreach (var session in SessionManager.Instance.GetAll())
    await session.DisconnectAsync();

await host.StopAsync();
