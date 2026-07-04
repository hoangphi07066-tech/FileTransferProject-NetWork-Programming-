using System.Net;
using System.Net.Sockets;
using Library;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options => options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors("AllowAll");

// Sử dụng Path.Combine để tương thích cả dấu \ của Windows và / của macOS
string storagePath = Path.Combine(Directory.GetCurrentDirectory(), "storage");
if (!Directory.Exists(storagePath)) Directory.CreateDirectory(storagePath);

Task.Run(() => StartTcpServer(11000, storagePath));

app.MapGet("/admin.html", () => {
    string filePath = Path.Combine(Directory.GetCurrentDirectory(), "admin.html");
    return Results.File(filePath, "text/html");
});

app.MapGet("/api/files", () => {
    var files = Directory.GetFiles(storagePath).Select(Path.GetFileName);
    return Results.Ok(files);
});

// Mở luồng cho toàn bộ mạng nội bộ
app.Run("http://0.0.0.0:5001");

void StartTcpServer(int port, string saveDir)
{
    // Lắng nghe TCP trên IPAddress.Any (Tất cả Card mạng)
    TcpListener listener = new(IPAddress.Any, port);
    listener.Start();
    Console.WriteLine($"[Server TCP] Đang chờ thiết bị đa nền tảng tại cổng {port}...");

    while (true)
    {
        try
        {
            TcpClient client = listener.AcceptTcpClient();
            Task.Run(() => HandleClient(client, saveDir));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Lỗi kết nối Socket: {ex.Message}");
        }
    }
}

// Sửa đổi trong hàm HandleClient của file s.cs
void HandleClient(TcpClient client, string saveDir)
{
    using var stream = client.GetStream();
    using var reader = new BinaryReader(stream);
    FileStream? fs = null;
    string currentFileName = "";

    try
    {
        while (client.Connected)
        {
            if (stream.DataAvailable || true)
            {
                int packetLength = reader.ReadInt32();
                byte[] packetBytes = reader.ReadBytes(packetLength);
                var packet = PacketHelper.Deserialize(packetBytes);

                // Tạo thư mục riêng cho từng người dùng (ví dụ: storage/user1)
                string userDir = Path.Combine(saveDir, packet.Username);
                if (!Directory.Exists(userDir)) Directory.CreateDirectory(userDir);

                // --- XỬ LÝ LỆNH THU HỒI FILE ---
                if (packet.FileSize == -1) 
                {
                    string targetPath = Path.Combine(userDir, packet.FileName);
                    if (File.Exists(targetPath)) 
                    {
                        File.Delete(targetPath);
                        Console.WriteLine($"[Hệ thống] User {packet.Username} đã thu hồi tệp: {packet.FileName}");
                    }
                    break;
                }

                // --- XỬ LÝ NHẬN FILE ---
                if (fs == null)
                {
                    currentFileName = packet.FileName;
                    string fullPath = Path.Combine(userDir, currentFileName);
                    // Dùng chế độ ghi đè, và luồng an toàn
                    fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                    Console.WriteLine($"\n[Server] [{packet.Username}] đang gửi: {currentFileName}");
                }

                fs.Write(packet.Data, 0, packet.Data.Length);
                
                if (packet.IsLastChunk)
                {
                    Console.WriteLine($"[Server] Đã nhận xong file của [{packet.Username}]: {currentFileName}");
                    break;
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Lỗi đường truyền: {ex.Message}");
    }
    finally
    {
        fs?.Close();
        client.Close();
    }
}