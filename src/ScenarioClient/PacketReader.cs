using System.Net.Sockets;
using GameServer.Packet;

namespace ScenarioClient;

public static class PacketReader
{
    public static async Task<(PacketId Id, byte[] Payload)> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[PacketHeader.HeaderSize];
        await stream.ReadExactlyAsync(header, ct);

        ushort size = BitConverter.ToUInt16(header, 0);
        var id = (PacketId)BitConverter.ToUInt16(header, 2);

        int payloadLength = size - PacketHeader.HeaderSize;
        if (payloadLength <= 0) return (id, Array.Empty<byte>());

        var payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, ct);
        return (id, payload);
    }
}
