namespace Library;
public class TransferPacket {
    public string FileName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int ChunkIndex { get; set; }
    public bool IsLastChunk { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
}