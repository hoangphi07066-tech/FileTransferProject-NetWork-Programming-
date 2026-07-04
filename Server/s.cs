using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.FileProviders;
using Library;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options => options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors("AllowAll");

// Khởi tạo thư mục lưu trữ
string storagePath = Path.Combine(Directory.GetCurrentDirectory(), "storage");
if (!Directory.Exists(storagePath)) Directory.CreateDirectory(storagePath);

// --- HỆ THỐNG LƯU TRỮ BÌNH LUẬN ---
string metadataPath = Path.Combine(storagePath, "_metadata.json");
ConcurrentDictionary<string, string> fileComments = new();

// Tải dữ liệu bình luận cũ (nếu có)
if (File.Exists(metadataPath))
{
    try
    {
        string json = File.ReadAllText(metadataPath);
        fileComments = JsonSerializer.Deserialize<ConcurrentDictionary<string, string>>(json) ?? new();
    }
    catch { }
}

void SaveMetadata()
{
    File.WriteAllText(metadataPath, JsonSerializer.Serialize(fileComments));
}

// Chạy TCP Server ở luồng nền
_ = StartTcpServerAsync(11000, storagePath);

// Cho phép truy cập file tĩnh để tính năng "Xem File (👁️)" trên Admin hoạt động
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(storagePath),
    RequestPath = "/storage"
});

// --- HTTP ENDPOINTS CHO ADMIN WEB ---

app.MapGet("/admin.html", () => {
    string filePath = Path.Combine(Directory.GetCurrentDirectory(), "admin.html");
    return Results.File(filePath, "text/html");
});

// 1. API: Lấy toàn bộ dữ liệu hệ thống cho Dashboard
app.MapGet("/api/admin/system-data", () => {
    var result = new List<object>();
    var userDirs = Directory.GetDirectories(storagePath);
    
    foreach (var dir in userDirs)
    {
        string clientName = Path.GetFileName(dir);
        var filesInfo = new List<object>();
        
        foreach (var filePath in Directory.GetFiles(dir))
        {
            var fileInfo = new FileInfo(filePath);
            string fileName = fileInfo.Name;
            
            // Dùng tên file làm ID. Lấy comment tương ứng nếu có.
            string commentKey = $"{clientName}|{fileName}";
            string comment = fileComments.TryGetValue(commentKey, out var c) ? c : "";

            filesInfo.Add(new {
                id = fileName, 
                name = fileName,
                size = fileInfo.Length,
                uploadTime = fileInfo.CreationTime.ToString("yyyy-MM-ddTHH:mm:ss"),
                status = "Hoàn tất",
                comment = comment
            });
        }
        
        // Chỉ thêm người dùng vào Dashboard nếu họ có file
        if (filesInfo.Count > 0)
        {
            result.Add(new {
                clientName = clientName,
                files = filesInfo
            });
        }
    }
    
    return Results.Ok(result);
});

// 2. API: Xoá File từ Web Admin
app.MapPost("/api/admin/delete", (DeleteRequest req) => {
    string targetPath = Path.Combine(storagePath, req.clientName, req.fileName);
    if (File.Exists(targetPath))
    {
        File.Delete(targetPath);
        
        // Xoá luôn comment của file này cho sạch bộ nhớ
        string commentKey = $"{req.clientName}|{req.fileName}";
        fileComments.TryRemove(commentKey, out _);
        SaveMetadata();
        
        return Results.Ok(new { message = "Xoá thành công" });
    }
    return Results.NotFound(new { message = "Không tìm thấy file" });
});

// 3. API: Lưu bình luận từ Web Admin
app.MapPost("/api/admin/comment", (CommentRequest req) => {
    string commentKey = $"{req.clientName}|{req.fileId}";
    fileComments[commentKey] = req.comment;
    SaveMetadata();
    
    return Results.Ok(new { message = "Lưu phản hồi thành công" });
});

// 4. API: Lấy kích thước file hiện tại (Dùng cho Resume)
app.MapGet("/api/admin/check-file", (string user, string filename) => {
    string targetPath = Path.Combine(storagePath, user, filename);
    if (File.Exists(targetPath))
    {
        return Results.Ok(new { offset = new FileInfo(targetPath).Length });
    }
    return Results.Ok(new { offset = 0 });
});

// Mở luồng cho toàn bộ mạng nội bộ
app.Run("http://127.0.0.1:5001");

// --- LOGIC TCP SERVER (Giữ nguyên của bạn) ---

async Task StartTcpServerAsync(int port, string saveDir)
{
    TcpListener listener = new(IPAddress.Any, port);
    listener.Start();
    Console.WriteLine($"[Server TCP] Đang chờ thiết bị đa nền tảng tại cổng {port}...");

    while (true)
    {
        try
        {
            TcpClient client = await listener.AcceptTcpClientAsync();
            _ = HandleClientAsync(client, saveDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Lỗi kết nối Socket: {ex.Message}");
        }
    }
}

async Task HandleClientAsync(TcpClient client, string saveDir)
{
    using var stream = client.GetStream();
    using var reader = new BinaryReader(stream);
    FileStream? fs = null;
    string currentFileName = "";
    string expectedHash = "";

    try
    {
        while (client.Connected)
        {
            byte[] lengthBuffer = new byte[4];
            int readLength = 0;
            while (readLength < 4)
            {
                int r = await stream.ReadAsync(lengthBuffer, readLength, 4 - readLength);
                if (r == 0) break;
                readLength += r;
            }
            if (readLength < 4) break;

            int packetLength = BitConverter.ToInt32(lengthBuffer, 0);
            byte[] packetBytes = new byte[packetLength];
            int totalRead = 0;
            while (totalRead < packetLength)
            {
                int r = await stream.ReadAsync(packetBytes, totalRead, packetLength - totalRead);
                if (r == 0) break;
                totalRead += r;
            }
            if (totalRead < packetLength) break;

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
                    
                    if (packet.IsResume)
                    {
                        fs = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                        Console.WriteLine($"\n[Server] [{packet.Username}] đang TẢI TIẾP: {currentFileName}");
                    }
                    else
                    {
                        fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                        Console.WriteLine($"\n[Server] [{packet.Username}] đang gửi: {currentFileName}");
                    }
                }

                await fs.WriteAsync(packet.Data, 0, packet.Data.Length);
                if (!string.IsNullOrEmpty(packet.FileHash)) expectedHash = packet.FileHash;
                
                if (packet.IsLastChunk)
                {
                    Console.WriteLine($"[Server] Đã nhận xong file của [{packet.Username}]: {currentFileName}");
                    
                    // Xác thực SHA256
                    fs.Close();
                    fs = null; // Để khối finally không cần đóng lại
                    string fullPath = Path.Combine(userDir, currentFileName);
                    
                    if (!string.IsNullOrEmpty(expectedHash))
                    {
                        using var sha256 = SHA256.Create();
                        using var hashStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read);
                        byte[] hashBytes = sha256.ComputeHash(hashStream);
                        string computedHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                        
                        if (computedHash == expectedHash)
                        {
                            Console.WriteLine($"[Server] Xác thực SHA256 thành công cho: {currentFileName}");
                        }
                        else
                        {
                            Console.WriteLine($"[Cảnh báo] Dữ liệu bị lỗi. SHA256 không khớp cho: {currentFileName}!");
                            hashStream.Close(); // Close trước khi xóa
                            File.Delete(fullPath);
                            Console.WriteLine($"[Hệ thống] Đã xoá file lỗi: {currentFileName}");
                        }
                    }
                    break;
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

// --- CLASS MODELS ĐỂ HỨNG DỮ LIỆU TỪ WEB ADMIN ---
class DeleteRequest 
{ 
    public string clientName { get; set; } = ""; 
    public string fileId { get; set; } = ""; 
    public string fileName { get; set; } = ""; 
}

class CommentRequest 
{ 
    public string clientName { get; set; } = ""; 
    public string fileId { get; set; } = ""; 
    public string comment { get; set; } = ""; 
}