using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Library;

Console.WriteLine("=== CLIENT TRUYỀN FILE DUNG LƯỢNG LỚN ===");
string serverIp = "127.0.0.1";
int port = 8888;
int numberOfThreads = 4; 

Console.Write("Kéo thả file cần truyền vào đây (hoặc nhập đường dẫn): ");
string? inputPath = Console.ReadLine();

if (string.IsNullOrWhiteSpace(inputPath))
{
    Console.WriteLine("[-] Bạn chưa nhập đường dẫn file!");
    return;
}

string filePath = inputPath.Replace("\"", "").Replace("'", "").Trim();

FileInfo fileInfo = new FileInfo(filePath);
if (!fileInfo.Exists)
{
    Console.WriteLine($"[-] Không tìm thấy file '{filePath}'. Vui lòng kiểm tra lại đường dẫn!");
    return;
}

string saveFileName = fileInfo.Name; 
long totalFileSize = fileInfo.Length;
int chunkSize = 1024 * 1024;

Console.WriteLine($"[+] Đã chọn file: {saveFileName}");
Console.WriteLine($"[+] Định dạng: {fileInfo.Extension}");
Console.WriteLine($"[+] Dung lượng: {totalFileSize / 1024 / 1024} MB");
Console.WriteLine("--------------------------------------------------");

HashSet<long> completedOffsets = new HashSet<long>();
try
{
    Console.WriteLine("[*] Đang kiểm tra tiến độ cũ với Server...");
    using TcpClient checkClient = new TcpClient();
    await checkClient.ConnectAsync(serverIp, port);
    
    TransferPacket reqHeader = new TransferPacket { Command = "REQ_STATE", FileName = saveFileName };
    await PacketHelper.SendPacketAsync(checkClient.GetStream(), reqHeader, Array.Empty<byte>());

    TransferPacket? resHeader = await PacketHelper.ReceiveHeaderAsync(checkClient.GetStream());
    if (resHeader != null && resHeader.Command == "RES_STATE")
    {
        byte[] stateBuffer = new byte[resHeader.ChunkSize];
        int totalRead = 0;
        while (totalRead < resHeader.ChunkSize)
        {
            int read = await checkClient.GetStream().ReadAsync(stateBuffer, totalRead, resHeader.ChunkSize - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        string stateJson = Encoding.UTF8.GetString(stateBuffer);
        completedOffsets = JsonSerializer.Deserialize<HashSet<long>>(stateJson) ?? new HashSet<long>();
        Console.WriteLine($"[+] Server báo đã nhận thành công {completedOffsets.Count} khối dữ liệu từ trước.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[-] Không thể lấy trạng thái. Sẽ bắt đầu truyền lại từ đầu. Chi tiết lỗi: {ex.Message}");
}

ConcurrentQueue<long> chunkQueue = new ConcurrentQueue<long>();
for (long offset = 0; offset < totalFileSize; offset += chunkSize)
{
    if (!completedOffsets.Contains(offset))
    {
        chunkQueue.Enqueue(offset);
    }
}
Console.WriteLine($"[*] Cần truyền {chunkQueue.Count} khối. Chuẩn bị khởi chạy đa luồng...");
List<Task> tasks = new List<Task>();
for (int i = 0; i < numberOfThreads; i++)
{
    int threadIndex = i + 1;
    tasks.Add(Task.Run(() => ConnectAndSendAsync(serverIp, port, threadIndex, chunkQueue, filePath, saveFileName, totalFileSize, chunkSize)));
}

await Task.WhenAll(tasks);
Console.WriteLine("\n[+] TOÀN BỘ FILE ĐÃ ĐƯỢC TRUYỀN XONG!");

//    xu li tung luong
async Task ConnectAndSendAsync(string ip, int port, int threadIndex, ConcurrentQueue<long> queue, string sourceFile, string targetFile, long totalSize, int cSize)
{
    TcpClient client = new TcpClient();
    bool isConnected = false;
    while (queue.TryDequeue(out long chunkOffset))
    {
        bool isChunkSent = false;
        while (!isChunkSent)
        {
            try
            {
                if (!isConnected)
                {
                    client = new TcpClient();
                    await client.ConnectAsync(ip, port);
                    isConnected = true;
                }

                NetworkStream stream = client.GetStream();
                
                byte[] payloadBytes = await FileHelper.ReadChunkAsync(sourceFile, chunkOffset, cSize);
                
                if (payloadBytes.Length == 0)
                {
                    isChunkSent = true;
                    continue;
                }

                string chunkHash = FileHelper.CalculateHash(payloadBytes);
                TransferPacket header = new TransferPacket
                {
                    Command = "DATA",
                    FileName = targetFile,
                    TotalSize = totalSize,
                    ChunkOffset = chunkOffset,
                    ChunkSize = payloadBytes.Length,
                    Checksum = chunkHash
                };

                await PacketHelper.SendPacketAsync(stream, header, payloadBytes);
                Console.WriteLine($"[Luồng {threadIndex}] Đã gửi xong Offset {chunkOffset} ({payloadBytes.Length} bytes)");
                
                isChunkSent = true;
            }
            catch (SocketException)
            {
                Console.WriteLine($"[-] [Luồng {threadIndex}] Mất kết nối! Đang thử gửi lại Offset {chunkOffset} sau 3 giây...");
                isConnected = false;
                await Task.Delay(3000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] [Luồng {threadIndex}] Lỗi hệ thống: {ex.Message}");
                break;
            }
        }
    }
}
