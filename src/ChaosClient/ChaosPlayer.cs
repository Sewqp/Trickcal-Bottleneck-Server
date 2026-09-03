using System.Net.Sockets;
using System.Text;
using GameServer.Packet;

namespace ChaosClient;

public static class ChaosPlayer
{
    public static async Task RunAsync(int index, ChaosOptions options, CancellationToken ct)
    {
        // ChurnRatio 비율만큼은 "재접속 churn" 플레이어 — 하트비트로 오래 붙어있는 대신 짧은 간격으로
        // 접속→로그인→끊기를 반복해서 SessionManager/PlayerGrain 구독-해제, redis reconnect 토큰 발급,
        // DisconnectAsync의 flush+길드+매칭취소 경로를 짧은 주기로 계속 때린다.
        bool isChurner = Random.Shared.NextDouble() < options.ChurnRatio;
        string name = $"chaos_{index}_{Guid.NewGuid():N}"[..24];

        while (!ct.IsCancellationRequested)
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(options.Host, options.Port, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                ChaosStats.IncrementConnectFailed();
                return;
            }

            ChaosStats.IncrementConnected();
            var stream = client.GetStream();

            try
            {
                await LoginAsync(name, stream, ct);

                if (isChurner)
                {
                    int alive = Random.Shared.Next(options.ChurnMinMs, options.ChurnMaxMs);
                    await Task.Delay(alive, ct);
                    ChaosStats.IncrementReconnectCycles();
                    // 루프 맨 위로 돌아가 즉시 재접속 — 정상 종료(연결 닫기)로 다음 사이클을 유도
                }
                else
                {
                    await HeartbeatLoopAsync(stream, options, ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                ChaosStats.IncrementDisconnected();
                return; // 스티디 플레이어는 끊기면 그대로 종료(재시도 안 함) — DummyClient와 동일 정책
            }

            if (!isChurner) return; // 스티디 루프가 정상 종료됐으면(캔슬 등) 여기서 끝
        }
    }

    private static async Task LoginAsync(string name, NetworkStream stream, CancellationToken ct)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var payload = new byte[2 + nameBytes.Length];
        BitConverter.TryWriteBytes(payload.AsSpan(0), (ushort)nameBytes.Length);
        nameBytes.CopyTo(payload.AsSpan(2));

        await stream.WriteAsync(PacketWriter.Build(PacketId.LoginRequest, payload), ct);

        var (id, result) = await PacketReader.ReadPacketAsync(stream, ct);
        if (id != PacketId.LoginResult || result.Length < 1 || result[0] != 1)
        {
            ChaosStats.IncrementLoginFailed();
            throw new IOException("Login failed");
        }

        ChaosStats.IncrementLoginSuccess();
    }

    // DummyClient와 다른 점: 매 하트비트마다 간격을 새로 랜덤 추첨함 — 플레이어별로 주기가 일정하지
    // 않고, 같은 플레이어 안에서도 짧게 왔다가 길게 뜸해지는 식으로 흔들리게 만들기 위함.
    private static async Task HeartbeatLoopAsync(NetworkStream stream, ChaosOptions options, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int interval = Random.Shared.Next(options.HeartbeatMinMs, options.HeartbeatMaxMs);
            await Task.Delay(interval, ct);

            await stream.WriteAsync(PacketWriter.Build(PacketId.Heartbeat), ct);
            ChaosStats.IncrementHeartbeatSent();

            var (id, _) = await PacketReader.ReadPacketAsync(stream, ct);
            if (id == PacketId.Heartbeat)
                ChaosStats.IncrementHeartbeatAckReceived();
        }
    }
}
