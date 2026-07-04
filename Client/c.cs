using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http.Features; // Thêm thư viện này
using Library;

var builder = WebApplication.CreateBuilder(args);

// 1. TẮT GIỚI HẠN DUNG LƯỢNG FILE UPLOAD CỦA ASP.NET CORE
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = null; // Bỏ giới hạn size request
});
builder.Services.Configure<FormOptions>(options =>
{
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartBodyLengthLimit = long.MaxValue; // Bỏ giới hạn size file
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

builder.Services.AddCors(options => options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors("AllowAll");

ConcurrentDictionary<string, List<UploadHistoryItem>> multiClientHistory = new();
string serverIp = "127.0.0.1";

app.MapGet("/client.html", () => {
    return Results.File(Path.Combine(Directory.GetCurrentDirectory(), "client.html"), "text/html");
});

app.MapGet("/api/history", (string user) => {
    var history = multiClientHistory.GetValueOrDefault(user ?? "Khách", new List<UploadHistoryItem>());
    return Results.Ok(history);
});

app.MapPost("/api/upload", async (HttpContext context) => {
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    var username = form["username"].ToString() ?? "Khách";
    
    if (file == null || file.Length == 0) return Results.BadRequest("Tệp tin trống.");

    var tempPath = Path.GetTempFileName();
    using (var stream = new FileStream(tempPath, FileMode.Create)) {
        await file.CopyToAsync(stream);
    }

    try
    {
        using TcpClient socketClient = new(serverIp, 11000);
        using var networkStream = socketClient.GetStream();
        using var writer = new BinaryWriter(networkStream);

        long totalSize = file.Length;
        int chunkIndex = 0;
        long bytesSent = 0;

        // 2. SỬ DỤNG STREAMING ĐỂ TRUYỀN FILE LỚN (Không dùng .ToList() gây tràn RAM)
        using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
        int bufferSize = 81920; // Kích thước mỗi gói tin (80KB)
        byte[] buffer = new byte[bufferSize];
        int bytesRead;

        while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            bytesSent += bytesRead;
            bool isLastChunk = (bytesSent == totalSize);

            // Chỉ lấy đúng số byte đọc được (tránh bị dính mảng byte rỗng ở cuối)
            byte[] actualData = new byte[bytesRead];
            Array.Copy(buffer, actualData, bytesRead);

            var packet = new TransferPacket
            {
                Username = username,
                FileName = file.FileName,
                FileSize = totalSize,
                ChunkIndex = chunkIndex++,
                IsLastChunk = isLastChunk,
                Data = actualData
            };

            byte[] serializedPacket = PacketHelper.Serialize(packet);
            writer.Write(serializedPacket.Length);
            writer.Write(serializedPacket);
            writer.Flush();
        }

        var userHistory = multiClientHistory.GetOrAdd(username, _ => new List<UploadHistoryItem>());
        userHistory.Add(new UploadHistoryItem(Guid.NewGuid().ToString(), file.FileName, totalSize, DateTime.Now, "Thành công"));

        return Results.Ok(new { message = "Upload thành công!" });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Lỗi kết nối Socket: {ex.Message}");
    }
    finally
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }
});

app.MapPost("/api/revoke", (RevokeRequest request) => {
    // ... (Phần code Revoke giữ nguyên như bản trước) ...
    var userHistory = multiClientHistory.GetValueOrDefault(request.Username, new List<UploadHistoryItem>());
    var item = userHistory.FirstOrDefault(x => x.Id == request.Id);
    if (item == null) return Results.NotFound("Không tìm thấy tệp.");

    try
    {
        using TcpClient socketClient = new(serverIp, 11000);
        using var networkStream = socketClient.GetStream();
        using var writer = new BinaryWriter(networkStream);

        var deleteCommandPacket = new TransferPacket
        {
            Username = request.Username,
            FileName = item.FileName,
            FileSize = -1,
            ChunkIndex = 0,
            IsLastChunk = true,
            Data = Array.Empty<byte>()
        };

        byte[] serializedPacket = PacketHelper.Serialize(deleteCommandPacket);
        writer.Write(serializedPacket.Length);
        writer.Write(serializedPacket);
        writer.Flush();

        int index = userHistory.FindIndex(x => x.Id == request.Id);
        userHistory[index] = item with { Status = "Đã thu hồi" };

        return Results.Ok(new { message = "Lệnh thu hồi thành công." });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Lỗi Socket: {ex.Message}");
    }
});

app.Run("http://0.0.0.0:5000");

public record UploadHistoryItem(string Id, string FileName, long FileSize, DateTime UploadTime, string Status);
public record RevokeRequest(string Username, string Id);