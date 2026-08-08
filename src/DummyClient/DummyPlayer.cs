using System.Net.Sockets;
using System.Text;
using GameServer.Packet;

namespace DummyClient;

public static class DummyPlayer
{
    public static async Task RunAsync(int index, StressOptions options, CancellationToken ct)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(options.Host, options.Port, ct);
        }
        catch
        {
            StressStats.IncrementConnectFailed();
            return;
        }

        StressStats.IncrementConnected();
        var stream = client.GetStream();

        try
        {
            await LoginAsync(index, stream, ct);
            await HeartbeatLoopAsync(stream, options, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            StressStats.IncrementDisconnected();
        }
    }

    private static async Task LoginAsync(int index, NetworkStream stream, CancellationToken ct)
    {
        var nameBytes = Encoding.UTF8.GetBytes($"dummy_{index}_{Guid.NewGuid():N}"[..24]);
        var payload = new byte[2 + nameBytes.Length];
        BitConverter.TryWriteBytes(payload.AsSpan(0), (ushort)nameBytes.Length);
        nameBytes.CopyTo(payload.AsSpan(2));

        await stream.WriteAsync(PacketWriter.Build(PacketId.LoginRequest, payload), ct);

        var (id, result) = await PacketReader.ReadPacketAsync(stream, ct);
        if (id != PacketId.LoginResult || result.Length < 1 || result[0] != 1)
        {
            StressStats.IncrementLoginFailed();
            throw new IOException("Login failed");
        }

        StressStats.IncrementLoginSuccess();
    }

    private static async Task HeartbeatLoopAsync(NetworkStream stream, StressOptions options, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(options.HeartbeatIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct);

            await stream.WriteAsync(PacketWriter.Build(PacketId.Heartbeat), ct);
            StressStats.IncrementHeartbeatSent();

            var (id, _) = await PacketReader.ReadPacketAsync(stream, ct);
            if (id == PacketId.Heartbeat)
                StressStats.IncrementHeartbeatAckReceived();
        }
    }
}
