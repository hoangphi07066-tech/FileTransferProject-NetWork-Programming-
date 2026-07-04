using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Library;

public static class PacketHelper
{
    public static async Task SendPacketAsync(NetworkStream stream, TransferPacket header, byte[] payload)
    {
        string jsonHeader = JsonSerializer.Serialize(header);
        byte[] headerBytes = Encoding.UTF8.GetBytes(jsonHeader);
        byte[] lengthBytes = BitConverter.GetBytes(headerBytes.Length);
        await stream.WriteAsync(lengthBytes, 0, lengthBytes.Length);
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
        
        if (payload != null && payload.Length > 0)
        {
            await stream.WriteAsync(payload, 0, payload.Length);
        }
    }
    public static async Task<TransferPacket?> ReceiveHeaderAsync(NetworkStream stream)
    {
        byte[] lengthBuffer = new byte[4];
        int bytesRead = await stream.ReadAsync(lengthBuffer, 0, 4);
        if (bytesRead < 4) return null;
        int headerLength = BitConverter.ToInt32(lengthBuffer, 0);
        byte[] headerBuffer = new byte[headerLength];
        int totalHeaderRead = 0;
        while (totalHeaderRead < headerLength)
        {
            int read = await stream.ReadAsync(headerBuffer, totalHeaderRead, headerLength - totalHeaderRead);
            if (read == 0) return null;
            totalHeaderRead += read;
        }

        string jsonString = Encoding.UTF8.GetString(headerBuffer);
        return JsonSerializer.Deserialize<TransferPacket>(jsonString);
    }
}
