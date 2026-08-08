using System.Text;
using GameServer.DB.Model;
using GameServer.DB.Repository;
using GameServer.Network;

namespace GameServer.Packet.Handler;

public static class ItemHandler
{
    private const int EntryHeaderSize = 8 + 4 + 1 + 4 + 2; // inventoryId+itemDefId+itemType+count+nameLen

    public static async Task HandleAcquireAsync(ClientSession session, Memory<byte> packet)
    {
        if (packet.Length < PacketHeader.HeaderSize + 8) return;
        var p = packet.Span[PacketHeader.HeaderSize..];
        int itemDefId = BitConverter.ToInt32(p);
        int count = BitConverter.ToInt32(p[4..]);

        long inventoryId = await ItemRepository.Instance.AcquireItemAsync(session.PlayerId, itemDefId, count);

        var result = new byte[9];
        result[0] = 1;
        BitConverter.TryWriteBytes(result.AsSpan(1), inventoryId);
        await session.SendAsync(PacketWriter.Build(PacketId.ItemAcquireResult, result));
    }

    public static async Task HandleInventoryRequestAsync(ClientSession session, Memory<byte> packet)
    {
        var items = await ItemRepository.Instance.GetInventoryAsync(session.PlayerId);
        await session.SendAsync(PacketWriter.Build(PacketId.InventoryResult, BuildInventoryPayload(items)));
    }

    public static async Task HandleUseAsync(ClientSession session, Memory<byte> packet)
    {
        if (packet.Length < PacketHeader.HeaderSize + 12) return;
        var p = packet.Span[PacketHeader.HeaderSize..];
        long inventoryId = BitConverter.ToInt64(p);
        int count = BitConverter.ToInt32(p[8..]);

        bool success = await ItemRepository.Instance.UseItemAsync(session.PlayerId, inventoryId, count);
        await session.SendAsync(PacketWriter.Build(PacketId.ItemUseResult, [(byte)(success ? 1 : 0)]));
    }

    // payload: [2B itemCount] + N * { [8B inventoryId][4B itemDefId][1B itemType][4B count][2B nameLen][nameBytes] }
    // MAX_PACKET_SIZE(512B) 제약상 한 응답에 담을 수 있는 아이템 종류 수는 제한됨 — 대량 인벤토리는 별도 페이징 필요
    private static byte[] BuildInventoryPayload(List<InventoryItemModel> items)
    {
        var nameBytesList = items.Select(item => Encoding.UTF8.GetBytes(item.ItemName)).ToList();
        int size = 2 + items.Count * EntryHeaderSize + nameBytesList.Sum(b => b.Length);
        var buf = new byte[size];
        int offset = 2;
        BitConverter.TryWriteBytes(buf.AsSpan(0), (ushort)items.Count);

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var nameBytes = nameBytesList[i];

            BitConverter.TryWriteBytes(buf.AsSpan(offset), item.InventoryId); offset += 8;
            BitConverter.TryWriteBytes(buf.AsSpan(offset), item.ItemDefId); offset += 4;
            buf[offset] = item.ItemType; offset += 1;
            BitConverter.TryWriteBytes(buf.AsSpan(offset), item.ItemCount); offset += 4;
            BitConverter.TryWriteBytes(buf.AsSpan(offset), (ushort)nameBytes.Length); offset += 2;
            nameBytes.CopyTo(buf.AsSpan(offset)); offset += nameBytes.Length;
        }

        return buf;
    }
}
