namespace Library;

public class TransferPacket
{
    public string Command { get; set; } = string.Empty; // VD: "REQ_SEND", "DATA", "PAUSE"
    public string FileName { get; set; } = string.Empty;
    public long TotalSize { get; set; }                 // Tổng dung lượng file
    public long ChunkOffset { get; set; }               // Vị trí bắt đầu của khối data này
    public int ChunkSize { get; set; }                  // Kích thước của mảng byte đi kèm
    public string Checksum { get; set; } = string.Empty;// Mã băm SHA256 kiểm tra lỗi
}