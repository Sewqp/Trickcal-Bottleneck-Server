namespace GameServer.Packet;

public sealed class PacketBuffer
{
    public const int MaxPacketSize = 512;

    private readonly byte[] _buffer = new byte[MaxPacketSize * 4];
    private int _writePos;
    private int _readPos;

    public bool Write(ReadOnlySpan<byte> span)
    {
        if (span.Length > _buffer.Length - _writePos)
        {
            int readable = GetReadableSize();
            if (span.Length > _buffer.Length - readable) return false;
            _buffer.AsSpan(_readPos, readable).CopyTo(_buffer);
            _writePos = readable;
            _readPos = 0;
        }

        span.CopyTo(_buffer.AsSpan(_writePos));
        _writePos += span.Length;
        return true;
    }

    public Memory<byte>? TryAssemble()
    {
        int readable = GetReadableSize();
        if (readable < PacketHeader.HeaderSize) return null;

        ushort packetSize = BitConverter.ToUInt16(_buffer, _readPos);
        if (packetSize < PacketHeader.HeaderSize || packetSize > MaxPacketSize) return null;
        if (readable < packetSize) return null;

        var packet = new byte[packetSize];
        _buffer.AsSpan(_readPos, packetSize).CopyTo(packet);
        _readPos += packetSize;
        return packet.AsMemory();
    }

    public int GetReadableSize() => _writePos - _readPos;
}
