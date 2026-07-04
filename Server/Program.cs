using System.Net;
using System.Net.Sockets;
using System.Text;
using Library;

Console.WriteLine("=== SERVER TRUYỀN FILE ===");
int port = 8888;
object stateLock = new object();
TcpListener listener = new TcpListener(IPAddress.Any, port);
listener.Start();
Console.WriteLine($"[+] Đang lắng nghe tại Port {port}...");
while (true)
{
    TcpClient client = await listener.AcceptTcpClientAsync();
    Console.WriteLine($"[+] Có kết nối mới từ: {client.Client.RemoteEndPoint}");
    _ = Task.Run(() => HandleClientAsync(client));
}
async Task HandleClientAsync(TcpClient client)
{
    using (client)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            while (true)
            {
                TransferPacket? header = await PacketHelper.ReceiveHeaderAsync(stream);
                if (header == null) break;
                if (header.Command == "REQ_STATE")
                {
                    Console.WriteLine($"[*] Client yêu cầu kiểm tra trạng thái file: {header.FileName}");
                    string stateFilePath = Path.Combine(Directory.GetCurrentDirectory(), header.FileName + ".state.json");
                    
                    HashSet<long> completedOffsets;
                    lock (stateLock)
                    {
                        completedOffsets = StateHelper.LoadState(stateFilePath);
                    }
                    string stateJson = System.Text.Json.JsonSerializer.Serialize(completedOffsets);
                    byte[] statePayload = System.Text.Encoding.UTF8.GetBytes(stateJson);

                    TransferPacket responseHeader = new TransferPacket
                    {
                        Command = "RES_STATE",
                        FileName = header.FileName,
                        ChunkSize = statePayload.Length
                    };

                    await PacketHelper.SendPacketAsync(stream, responseHeader, statePayload);
                    continue;
                }
                if (header.Command == "REVOKE")
                {
                    Console.WriteLine($"[*] Client yêu cầu thu hồi file: {header.FileName}");
                    string savePath = Path.Combine(Directory.GetCurrentDirectory(), header.FileName);
                    string stateFilePath = savePath + ".state.json";
                    
                    bool isDeleted = false;
                    string errMsg = "";
                    lock (stateLock)
                    {
                        try 
                        {
                            if (File.Exists(savePath)) File.Delete(savePath);
                            if (File.Exists(stateFilePath)) File.Delete(stateFilePath);
                            Console.WriteLine($"[+] Đã xóa thành công file {header.FileName}");
                            isDeleted = true;
                        }
                        catch (Exception ex)
                        {
                            errMsg = ex.Message;
                            Console.WriteLine($"[-] Lỗi khi xóa file: {errMsg}");
                        }
                    }
                    
                    TransferPacket responseHeader = new TransferPacket
                    {
                        Command = "RES_REVOKE",
                        FileName = header.FileName,
                        Checksum = isDeleted ? "OK" : errMsg
                    };
                    await PacketHelper.SendPacketAsync(stream, responseHeader, Array.Empty<byte>());
                    continue;
                }
                if (header.Command == "DATA" && header.ChunkSize > 0)
                {
                    byte[] payloadBuffer = new byte[header.ChunkSize];
                    int totalRead = 0;
                    while (totalRead < header.ChunkSize)
                    {
                        int read = await stream.ReadAsync(payloadBuffer, totalRead, header.ChunkSize - totalRead);
                        if (read == 0) break;
                        totalRead += read;
                    }

                    if (FileHelper.CalculateHash(payloadBuffer) == header.Checksum)
                    {
                        string savePath = Path.Combine(Directory.GetCurrentDirectory(), header.FileName);
                        string stateFilePath = savePath + ".state.json";

                        await FileHelper.WriteChunkAsync(savePath, payloadBuffer, header.ChunkOffset);
                        Console.WriteLine($"[+] Đã lưu Offset {header.ChunkOffset}");
                        lock (stateLock)
                        {
                            var currentState = StateHelper.LoadState(stateFilePath);
                            currentState.Add(header.ChunkOffset);
                            StateHelper.SaveState(stateFilePath, currentState);
                        }
                    }
                }
            }
            
            Console.WriteLine($"[+] [Socket] Client {client.Client.RemoteEndPoint} đã hoàn thành và ngắt kết nối an toàn.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[-] Lỗi đột ngột từ Client: {ex.Message}");
        }
    }
}
