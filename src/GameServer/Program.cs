using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Dashboard;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Npgsql; // TracerProviderBuilderExtensions.AddNpgsql() — Npgsql.OpenTelemetry가 이 네임스페이스에 확장 메서드를 둠
using StackExchange.Redis;
using GameServer.Config;
using GameServer.DB;
using GameServer.Grains;
using GameServer.Logging;
using GameServer.Metrics;
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
ApmReporter.Instance.Init(config.ApmCollectorEndpoint, config.OrleansServiceId, cts.Token);

DbConnectionPool.Instance.Init(config.PostgresConnectionString);
Console.WriteLine("[DB] PostgreSQL connection pool ready.");

RedisClient.Instance.Init(config.RedisConnectionString);
Console.WriteLine("[DB] Redis connected.");

// Orleans Dashboard(app.MapOrleansDashboard())는 ASP.NET Core 엔드포인트라 일반 Host가 아니라
// WebApplication으로 띄워야 함 — Orleans 실로 구성(UseOrleans 등)은 IHostApplicationBuilder 확장이라
// WebApplicationBuilder에서도 그대로 동작함.
var hostBuilder = WebApplication.CreateBuilder(args);
hostBuilder.WebHost.UseUrls($"http://localhost:{config.DashboardPort}");
// Polly(Orleans의 배치/재시도 파이프라인)가 그레인 호출마다 Info 레벨로 로그를 남겨서, 동접이 많아지면
// 콘솔 출력량 자체가 병목이 됨(3만 명 로그인 테스트에서 실제로 확인됨) — Warning 이상만 남기도록 낮춤.
hostBuilder.Logging.AddFilter("Polly", Microsoft.Extensions.Logging.LogLevel.Warning);
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
    // 그레인 호출 사이에 Activity(트레이스 컨텍스트)를 전파 — 이게 없으면 OTel이 "Microsoft.Orleans.*"
    // 소스를 구독해도 각 그레인 호출이 서로 이어지지 않는 끊어진 span으로만 남음.
    silo.AddActivityPropagation();
    // 실로를 여러 개(멀티 실로 클러스터) 띄워서 실험할 때 각 실로/그레인 분포를 한 화면에서 보기 위함 —
    // 대시보드 서비스 자체는 모든 실로에 있어야 메트릭을 수집함(Microsoft 공식 문서 기준).
    silo.AddDashboard();
});

// OpenTelemetry — OTLP로 docker-compose의 apm-server(8200)에 트레이스 전송.
// PostgreSQL(Npgsql 내장 ActivitySource)/Redis(StackExchangeRedis 계측)/Orleans 그레인 호출
// 세 갈래를 한 파이프라인으로 묶음. 아직 미검증 상태였던 관측성 스택([[project-new-server-design]])을
// 처음으로 실제 코드와 연결하는 부분.
hostBuilder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(config.OrleansServiceId))
    .WithTracing(tracing => tracing
        .AddSource("Microsoft.Orleans.Runtime", "Microsoft.Orleans.Application")
        .AddNpgsql()
        .AddRedisInstrumentation(RedisClient.Instance.Multiplexer)
        .AddOtlpExporter(o => o.Endpoint = new Uri(config.OtlpEndpoint)));

using var host = hostBuilder.Build();
host.MapOrleansDashboard();
await host.StartAsync();
Console.WriteLine($"[Dashboard] http://localhost:{config.DashboardPort}");
OrleansClient.Instance.Init(host.Services.GetRequiredService<IGrainFactory>());
Console.WriteLine($"[Orleans] Silo active. Cluster={config.OrleansClusterId}");

_ = new HeartbeatManager(cts.Token).RunAsync();

var dispatcher = PacketDispatcher.Instance;
dispatcher.Register(PacketId.LoginRequest,       LoginHandler.HandleAsync);
dispatcher.Register(PacketId.Heartbeat,          HeartbeatHandler.HandleAsync);
dispatcher.Register(PacketId.ReconnectRequest,   ReconnectHandler.HandleAsync);
dispatcher.Register(PacketId.MatchRequest,       MatchHandler.HandleAsync);
dispatcher.Register(PacketId.MatchCancelRequest, MatchHandler.HandleCancelAsync);
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

dispatcher.Register(PacketId.DebugGrantCurrencyRequest, DebugHandler.HandleGrantCurrencyAsync);

var server = new TcpServer(config.TcpPort, cts.Token, config.TcpBacklog, config.TcpAcceptDelayMs);
await server.StartAsync();

// 서버 강제 종료(design-plan-draft.md 5절 트리거 ④) — 접속 중이던 세션 전부를 순회하며
// SessionFlush.FlushOneAsync를 반복 호출(ClientSession.DisconnectAsync 안에서 호출됨)
foreach (var session in SessionManager.Instance.GetAll())
    await session.DisconnectAsync();

await host.StopAsync();
