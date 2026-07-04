// using System.Net.Sockets;
// using System.Text;
// using Library;

// var builder = WebApplication.CreateBuilder(args);
// builder.Services.AddAntiforgery();
// var app = builder.Build();
// app.UseStaticFiles();
// app.UseAntiforgery();
// app.MapGet("/", async (HttpContext context) =>
// {
//     context.Response.ContentType = "text/html";
//     string indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "index.html");
//     if (!File.Exists(indexPath))
//     {
//         indexPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "index.html");
//     }
//     if (!File.Exists(indexPath))
//     {
//         indexPath = Path.Combine(Directory.GetCurrentDirectory(), "ClientWeb", "wwwroot", "index.html");
//     }

//     if (File.Exists(indexPath))
//     {
//         await context.Response.SendFileAsync(indexPath);
//     }
//     else
//     {
//         context.Response.StatusCode = 404;
//         await context.Response.WriteAsync($"[-] Khong tim thay file index.html! Duong dan hien tai da thu: {indexPath}");
//     }
// });

// app.MapPost("/api/upload-chunk", async (HttpContext context) =>
// {
//     try
//     {
//         var form = await context.Request.ReadFormAsync();
        
//         string? fileName = form["fileName"];
//         string? totalSizeStr = form["totalSize"];
//         string? chunkOffsetStr = form["chunkOffset"];
//         string? chunkSizeStr = form["chunkSize"];
//         var uploadedFile = form.Files["file"];
//         if (string.IsNullOrEmpty(fileName) || uploadedFile == null || 
//             !long.TryParse(totalSizeStr, out long totalSize) || 
//             !long.TryParse(chunkOffsetStr, out long chunkOffset) || 
//             !int.TryParse(chunkSizeStr, out int chunkSize))
//         {
//             return Results.BadRequest("Dữ liệu Form gửi lên bị thiếu hoặc không đúng định dạng.");
//         }

//         byte[] payloadBytes = new byte[uploadedFile.Length];
//         using (var streamReader = uploadedFile.OpenReadStream())
//         {
//             int totalRead = 0;
//             while (totalRead < payloadBytes.Length)
//             {
//                 int read = await streamReader.ReadAsync(payloadBytes, totalRead, payloadBytes.Length - totalRead);
//                 if (read == 0) break;
//                 totalRead += read;
//             }
//         }


//         string serverIp = "127.0.0.1";
//         int port = 8888;
//         using (TcpClient client = new TcpClient())
//         {
//             await client.ConnectAsync(serverIp, port);
//             using (NetworkStream stream = client.GetStream())
//             {
//                 string chunkHash = FileHelper.CalculateHash(payloadBytes);
//                 TransferPacket header = new TransferPacket
//                 {
//                     Command = "DATA",
//                     FileName = fileName,
//                     TotalSize = totalSize,
//                     ChunkOffset = chunkOffset,
//                     ChunkSize = payloadBytes.Length,
//                     Checksum = chunkHash
//                 };

//                 await PacketHelper.SendPacketAsync(stream, header, payloadBytes);
//             }
//         }

//         Console.WriteLine($"[+] Da chuyen tiep chunk thanh cong: {fileName} | Offset: {chunkOffset} ({payloadBytes.Length} bytes)");
//         return Results.Ok("Khối dữ liệu đã được xử lý và chuyển tiếp.");
//     }
//     catch (Exception ex)
//     {
//         Console.WriteLine($"[-] Gặp lỗi tại ClientWeb: {ex.Message}");
//         return Results.BadRequest(ex.Message);
//     }
// })
// .DisableAntiforgery();
// app.Run("http://localhost:5000");



using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;
using Library;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAntiforgery();
var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

// Bộ nhớ tạm thời lưu số liệu log để trang Admin hiển thị thời gian thực
var sharedAdminStats = new ConcurrentDictionary<string, (long totalSize, long lastOffset)>();
long totalChunksProcessed = 0;

// =========================================================================
// ROUTING CHUYỂN HƯỚNG GIAO DIỆN (TỰ ĐỘNG SỬA LỖI ĐƯỜNG DẪN TẬP TIN)
// =========================================================================
string GetSecurePath(string fileName)
{
    string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", fileName);
    if (!File.Exists(p)) p = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", fileName);
    if (!File.Exists(p)) p = Path.Combine(Directory.GetCurrentDirectory(), "ClientWeb", "wwwroot", fileName);
    return p;
}

app.MapGet("/", async (HttpContext ctx) => ctx.Response.Redirect("/login"));

app.MapGet("/login", async (HttpContext ctx) => {
    ctx.Response.ContentType = "text/html";
    await ctx.Response.SendFileAsync(GetSecurePath("login.html"));
});

app.MapGet("/client", async (HttpContext ctx) => {
    ctx.Response.ContentType = "text/html";
    await ctx.Response.SendFileAsync(GetSecurePath("client.html"));
});

app.MapGet("/admin", async (HttpContext ctx) => {
    ctx.Response.ContentType = "text/html";
    await ctx.Response.SendFileAsync(GetSecurePath("admin.html"));
});

// =========================================================================
// API XỬ LÝ CHỨC NĂNG CHÍNH
// =========================================================================

// 1. Chức năng xác thực Đăng nhập
app.MapPost("/api/auth/login", async (HttpContext ctx) => {
    var loginData = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    if (loginData != null && loginData.TryGetValue("username", out var user) && loginData.TryGetValue("password", out var pass))
    {
        if (user == "admin" && pass == "123") return Results.Json(new { role = "admin" });
        if (user == "client" && pass == "123") return Results.Json(new { role = "client" });
    }
    return Results.Unauthorized();
});

// 2. Chức năng lấy báo cáo hệ thống dành riêng cho Admin
app.MapGet("/api/admin/stats", () => {
    var fileList = sharedAdminStats.Select(kv => new {
        FileName = kv.Key,
        TotalSize = kv.Value.totalSize,
        LastOffset = kv.Value.lastOffset
    }).ToList();

    return Results.Json(new {
        TotalChunksReceived = Interlocked.Read(ref totalChunksProcessed),
        RecentFiles = fileList
    });
});

// 3. Chức năng tiếp nhận FormData từ Client gửi lên và bắn qua TCP Server (Port 8888)
app.MapPost("/api/upload-chunk", async (HttpContext context) =>
{
    try
    {
        var form = await context.Request.ReadFormAsync();
        string? fileName = form["fileName"];
        long totalSize = long.Parse(form["totalSize"]!);
        long chunkOffset = long.Parse(form["chunkOffset"]!);
        var uploadedFile = form.Files["file"];

        if (string.IsNullOrEmpty(fileName) || uploadedFile == null) return Results.BadRequest();

        // Chuyển file sang mảng bytes
        byte[] payloadBytes = new byte[uploadedFile.Length];
        using (var reader = uploadedFile.OpenReadStream())
        {
            await reader.ReadAsync(payloadBytes, 0, payloadBytes.Length);
        }

        // Cập nhật thông tin lưu vết giám sát cho trang Admin
        Interlocked.Increment(ref totalChunksProcessed);
        sharedAdminStats[fileName] = (totalSize, chunkOffset);

        // Đẩy Socket dữ liệu qua TCP Server đích
        string serverIp = "127.0.0.1";
        int port = 8888;
        using (TcpClient client = new TcpClient())
        {
            await client.ConnectAsync(serverIp, port);
            using (NetworkStream stream = client.GetStream())
            {
                string chunkHash = FileHelper.CalculateHash(payloadBytes);
                TransferPacket header = new TransferPacket
                {
                    Command = "DATA",
                    FileName = fileName,
                    TotalSize = totalSize,
                    ChunkOffset = chunkOffset,
                    ChunkSize = payloadBytes.Length,
                    Checksum = chunkHash
                };
                await PacketHelper.SendPacketAsync(stream, header, payloadBytes);
            }
        }
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).DisableAntiforgery(); // Tắt bộ chặn Form để nhận dữ liệu từ client.html

app.MapDelete("/api/revoke/{fileName}", async (string fileName) =>
{
    try
    {
        string serverIp = "127.0.0.1";
        int port = 8888;
        using (TcpClient client = new TcpClient())
        {
            await client.ConnectAsync(serverIp, port);
            using (NetworkStream stream = client.GetStream())
            {
                TransferPacket header = new TransferPacket
                {
                    Command = "REVOKE",
                    FileName = fileName
                };
                await PacketHelper.SendPacketAsync(stream, header, Array.Empty<byte>());
                
                // Chờ phản hồi từ Server
                TransferPacket? response = await PacketHelper.ReceiveHeaderAsync(stream);
                if (response == null || response.Command != "RES_REVOKE")
                {
                    return Results.BadRequest("Server từ chối lệnh xóa hoặc phiên bản Server chưa được cập nhật. Vui lòng restart Server.");
                }
                
                if (response.Checksum != "OK")
                {
                    return Results.BadRequest($"Server báo lỗi không thể xóa file: {response.Checksum}");
                }
            }
        }
        sharedAdminStats.TryRemove(fileName, out _); // Xóa khỏi bộ nhớ admin giám sát
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.Run("http://localhost:5500");