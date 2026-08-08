using System.Runtime.InteropServices;

namespace GameServer.Packet;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PacketHeader
{
    public ushort Size;
    public PacketId Id;

    public const int HeaderSize = sizeof(ushort) + sizeof(ushort); // 4 bytes
}
