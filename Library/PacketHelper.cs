using System.IO;
namespace Library;
public static class PacketHelper {
    public static byte[] Serialize(TransferPacket packet) {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(packet.Username); // Ghi Username
        writer.Write(packet.FileName); writer.Write(packet.FileSize);
        writer.Write(packet.ChunkIndex); writer.Write(packet.IsLastChunk);
        writer.Write(packet.Data.Length); writer.Write(packet.Data);
        return ms.ToArray();
    }
    public static TransferPacket Deserialize(byte[] bytes) {
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);
        var p = new TransferPacket {
            Username = reader.ReadString(), // Đọc Username
            FileName = reader.ReadString(), FileSize = reader.ReadInt64(),
            ChunkIndex = reader.ReadInt32(), IsLastChunk = reader.ReadBoolean()
        };
        p.Data = reader.ReadBytes(reader.ReadInt32());
        return p;
    }
}