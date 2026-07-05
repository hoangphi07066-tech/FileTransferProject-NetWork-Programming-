using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features; 
using Library;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

// 1. TẮT GIỚI HẠN DUNG LƯỢNG FILE UPLOAD CỦA ASP.NET CORE
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = null; 
});
builder.Services.Configure<FormOptions>(options =>
{
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartBodyLengthLimit = long.MaxValue; 
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

// HttpClient gọi sang API của Server Admin
builder.Services.AddHttpClient(); 

builder.Services.AddCors(options => options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors("AllowAll");

ConcurrentDictionary<string, List<UploadHistoryItem>> multiClientHistory = new();
ConcurrentDictionary<string, CancellationTokenSource> activeUploads = new();
string serverIp = "127.0.0.1";

app.MapGet("/client.html", () => {
    return Results.File(Path.Combine(Directory.GetCurrentDirectory(), "client.html"), "text/html");
});

app.MapGet("/api/check-progress", async (string user, string filename, IHttpClientFactory httpClientFactory) => {
    try {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5); // Giới hạn đợi 5 giây
        var response = await client.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:5001/api/admin/check-file-size?username={user}&filename={filename}"); //===============================================================================================
        return Results.Ok(response);
    } catch {
        return Results.Problem("Server lưu trữ đang ngoại tuyến", statusCode: 503);
    }
});

// ĐỒNG BỘ COMMENT TỪ SERVER ADMIN VỀ CLIENT
app.MapGet("/api/history", async (string user, IHttpClientFactory httpClientFactory) => {
    string username = user ?? "Khách";
    var history = multiClientHistory.GetValueOrDefault(username, new List<UploadHistoryItem>());
    var updatedHistory = history.ToList();

    try 
    {
        var client = httpClientFactory.CreateClient();
        var response = await client.GetFromJsonAsync<JsonElement>("http://127.0.0.1:5001/api/admin/system-data"); //===============================================================================================
        foreach (var clientData in response.EnumerateArray()) 
        {
            if (clientData.GetProperty("clientName").GetString() == username) 
            {
                var files = clientData.GetProperty("files").EnumerateArray();
                for (int i = 0; i < updatedHistory.Count; i++) 
                {
                    // Ghép cặp file theo tên client
                    var matchedFile = files.FirstOrDefault(x => x.GetProperty("name").GetString() == updatedHistory[i].FileName);
                    if (matchedFile.ValueKind != JsonValueKind.Undefined) 
                    {
                        string comment = matchedFile.GetProperty("comment").GetString() ?? "";
                        // Cập nhật comment vào Item
                        updatedHistory[i] = updatedHistory[i] with { Comment = comment };
                    }
                }
                
                // Lưu bản cập nhật vào bộ nhớ cache
                multiClientHistory[username] = updatedHistory;
                break;
            }
        }
    } 
    catch 
    {
        // Nếu Server chưa bật hoặc mất kết nối, vẫn trả về lịch sử cũ
    }
    
    return Results.Ok(updatedHistory);
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
    string fileHashString = "";
    using (var sha256 = SHA256.Create())
    using (var hashStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read))
    {
        byte[] hashBytes = sha256.ComputeHash(hashStream);
        fileHashString = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }
    string uploadId = Guid.NewGuid().ToString();
    var userHistory = multiClientHistory.GetOrAdd(username, _ => new List<UploadHistoryItem>());
    userHistory.Add(new UploadHistoryItem(uploadId, file.FileName, file.Length, DateTime.Now, "Đang tải", "", tempPath));
    var cts = new CancellationTokenSource();
    activeUploads[uploadId] = cts;
    try
    {
        using TcpClient socketClient = new(serverIp, 11000);
        using var networkStream = socketClient.GetStream();
        long totalSize = file.Length;
        int chunkIndex = 0;
        long bytesSent = 0;
        using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
        int bufferSize = 81920;
        byte[] buffer = new byte[bufferSize];
        int bytesRead;
        while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
        {
            bytesSent += bytesRead;
            bool isLastChunk = (bytesSent >= totalSize);
            byte[] actualData = new byte[bytesRead];
            Array.Copy(buffer, actualData, bytesRead);
            var packet = new TransferPacket
            {
                Username = username,
                FileName = file.FileName,
                FileSize = totalSize,
                ChunkIndex = chunkIndex++,
                IsLastChunk = isLastChunk,
                IsResume = false,
                FileHash = fileHashString,
                Data = actualData
            };

            byte[] serializedPacket = PacketHelper.Serialize(packet);
            byte[] lengthBytes = BitConverter.GetBytes(serializedPacket.Length);
            await networkStream.WriteAsync(lengthBytes, cts.Token);
            await networkStream.WriteAsync(serializedPacket, cts.Token);
            await networkStream.FlushAsync(cts.Token);
        }
        activeUploads.TryRemove(uploadId, out _);
        int idxOk = userHistory.FindIndex(x => x.Id == uploadId);
        if (idxOk >= 0) userHistory[idxOk] = userHistory[idxOk] with { Status = "Thành công" };
        if (File.Exists(tempPath)) File.Delete(tempPath);
        return Results.Ok(new { message = "Upload thành công!" });
    }
    catch (OperationCanceledException)
    {
        // Nút tạm dừng
        activeUploads.TryRemove(uploadId, out _);
        int idxPause = userHistory.FindIndex(x => x.Id == uploadId);
        if (idxPause >= 0) userHistory[idxPause] = userHistory[idxPause] with { Status = "Tạm dừng" };
        // Giữ file tạm
        return Results.Ok(new { message = "Upload tạm dừng." });
    }
    catch (Exception ex)
    {
        // Server mất kết nối đột ngột
        Console.WriteLine($"[Client] Lỗi Socket: {ex.Message}");
        activeUploads.TryRemove(uploadId, out _);
        int idxErr = userHistory.FindIndex(x => x.Id == uploadId);
        if (idxErr >= 0) userHistory[idxErr] = userHistory[idxErr] with { Status = "Mất kết nối" };
        // Giữ file tạm -> Resume
        return Results.Problem($"Mất kết nối tới Server: {ex.Message}");
    }
});

app.MapPost("/api/revoke", async (RevokeRequest request) => {
    var userHistory = multiClientHistory.GetValueOrDefault(request.Username, new List<UploadHistoryItem>());
    var item = userHistory.FirstOrDefault(x => x.Id == request.Id);
    if (item == null) return Results.NotFound("Không tìm thấy tệp.");

    try
    {
        using TcpClient socketClient = new(serverIp, 11000);
        using var networkStream = socketClient.GetStream();

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
        byte[] lengthBytes = BitConverter.GetBytes(serializedPacket.Length);
        await networkStream.WriteAsync(lengthBytes);
        await networkStream.WriteAsync(serializedPacket);
        await networkStream.FlushAsync();

        int index = userHistory.FindIndex(x => x.Id == request.Id);
        userHistory[index] = item with { Status = "Đã thu hồi" };

        return Results.Ok(new { message = "Lệnh thu hồi thành công." });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Lỗi Socket: {ex.Message}");
    }
});

app.MapPost("/api/pause", (RevokeRequest request) => {
    if (activeUploads.TryGetValue(request.Id, out var cts))
    {
        cts.Cancel();
        return Results.Ok(new { message = "Đã tạm dừng." });
    }
    return Results.NotFound("Không tìm thấy luồng tải.");
});

app.MapPost("/api/resume", async (RevokeRequest request, IHttpClientFactory httpClientFactory) => {
    var userHistory = multiClientHistory.GetValueOrDefault(request.Username, new List<UploadHistoryItem>());
    var itemIndex = userHistory.FindIndex(x => x.Id == request.Id);
    if (itemIndex == -1) return Results.NotFound("Không tìm thấy tệp.");
    var item = userHistory[itemIndex];

    if (!File.Exists(item.TempFilePath)) 
        return Results.NotFound("Tệp tin gốc không còn tồn tại trên máy.");

    userHistory[itemIndex] = item with { Status = "Đang tải" };
    var cts = new CancellationTokenSource();
    activeUploads[request.Id] = cts;

    // Chạy tải nền
    _ = Task.Run(async () => {
        bool isCompleted = false;
        int retryCount = 0;
        
        while (retryCount < 3 && !isCompleted && !cts.Token.IsCancellationRequested)
        {
            try
            {
                var client = httpClientFactory.CreateClient();
                var response = await client.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:5001/api/admin/check-file?user={request.Username}&filename={item.FileName}");
                long offset = response.GetProperty("offset").GetInt64();

                using TcpClient socketClient = new(serverIp, 11000);
                using var networkStream = socketClient.GetStream();

                long totalSize = item.FileSize;
                int chunkIndex = (int)(offset / 81920);
                long bytesSent = offset;

                using var fs = new FileStream(item.TempFilePath, FileMode.Open, FileAccess.Read);
                fs.Seek(offset, SeekOrigin.Begin);
                
                int bufferSize = 81920; 
                byte[] buffer = new byte[bufferSize];
                int bytesRead;
                
                while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
                {
                    bytesSent += bytesRead;
                    bool isLastChunk = (bytesSent >= totalSize);

                    byte[] actualData = new byte[bytesRead];
                    Array.Copy(buffer, actualData, bytesRead);

                    var packet = new TransferPacket
                    {
                        Username = request.Username,
                        FileName = item.FileName,
                        FileSize = totalSize,
                        ChunkIndex = chunkIndex++,
                        IsLastChunk = isLastChunk,
                        IsResume = true,
                        FileHash = "",
                        Data = actualData
                    };

                    byte[] serializedPacket = PacketHelper.Serialize(packet);
                    byte[] lengthBytes = BitConverter.GetBytes(serializedPacket.Length);
                    await networkStream.WriteAsync(lengthBytes, cts.Token);
                    await networkStream.WriteAsync(serializedPacket, cts.Token);
                    await networkStream.FlushAsync(cts.Token);
                }
                
                isCompleted = true;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi Socket Resume: {ex.Message}");
                retryCount++;
                if (retryCount < 3) await Task.Delay(3000);
            }
        }
        
        activeUploads.TryRemove(request.Id, out _);
        var finalIdx = userHistory.FindIndex(x => x.Id == request.Id);
        if (finalIdx >= 0)
        {
            if (isCompleted) {
                userHistory[finalIdx] = userHistory[finalIdx] with { Status = "Thành công" };
                if (File.Exists(item.TempFilePath)) File.Delete(item.TempFilePath);
            }
            else if (cts.Token.IsCancellationRequested)
                userHistory[finalIdx] = userHistory[finalIdx] with { Status = "Tạm dừng" };
            else
                userHistory[finalIdx] = userHistory[finalIdx] with { Status = "Mất kết nối" };
        }
    });

    return Results.Ok(new { message = "Đang tiến hành tải tiếp tục." });
});

app.Run("http://0.0.0.0:5000");


public record UploadHistoryItem(string Id, string FileName, long FileSize, DateTime UploadTime, string Status, string Comment = "", string TempFilePath = "");
public record RevokeRequest(string Username, string Id);