using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Library;

public static class FileHelper
{
    public static string CalculateHash(byte[] data)
    {
        byte[] hashBytes = SHA256.HashData(data);
        return Convert.ToHexString(hashBytes); 
    }
    public static async Task<byte[]> ReadChunkAsync(string filePath, long offset, int chunkSize)
    {
        using SafeFileHandle handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long fileLength = RandomAccess.GetLength(handle);
        if (offset >= fileLength) return Array.Empty<byte>();

        int actualChunkSize = (int)Math.Min(chunkSize, fileLength - offset);
        byte[] buffer = new byte[actualChunkSize];

        await RandomAccess.ReadAsync(handle, buffer, offset);
        return buffer;
    }
    public static async Task WriteChunkAsync(string filePath, byte[] data, long offset)
    {
        using SafeFileHandle handle = File.OpenHandle(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        await RandomAccess.WriteAsync(handle, data, offset);
    }
}
