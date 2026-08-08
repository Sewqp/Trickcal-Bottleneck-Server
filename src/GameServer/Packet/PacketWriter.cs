namespace GameServer.Packet;

public static class PacketWriter
{
    public static byte[] Build(PacketId id, ReadOnlySpan<byte> payload = default)
    {
        int size = PacketHeader.HeaderSize + payload.Length;
        var buf = new byte[size];
        BitConverter.TryWriteBytes(buf.AsSpan(0), (ushort)size);
        BitConverter.TryWriteBytes(buf.AsSpan(2), (ushort)id);
        if (payload.Length > 0)
            payload.CopyTo(buf.AsSpan(PacketHeader.HeaderSize));
        return buf;
    }
}
