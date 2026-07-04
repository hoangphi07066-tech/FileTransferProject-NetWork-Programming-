using System.IO;
namespace Library;
public static class FileHelper {
    public const int ChunkSize = 1024 * 128; // 128KB bộ đệm
    public static IEnumerable<byte[]> ReadChunks(string filePath) {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true);
        byte[] buffer = new byte[ChunkSize]; int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0) {
            byte[] c = new byte[read]; Buffer.BlockCopy(buffer, 0, c, 0, read); yield return c;
        }
    }
}