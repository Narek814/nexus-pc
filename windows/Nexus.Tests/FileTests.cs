using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Core.Apps;
using Nexus.Core.Files;
using Nexus.Core.Protocol;
using Xunit;

namespace Nexus.Tests;

public sealed class FileNameTests
{
    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("фото с отпуска.HEIC", "фото с отпуска.HEIC")]
    [InlineData("../../Windows/System32/evil.dll", "evil.dll")]
    [InlineData(@"C:\Users\Public\x.txt", "x.txt")]
    [InlineData("a<b>c:d\"e|f?g*.txt", "a_b_c_d_e_f_g_.txt")]
    [InlineData("report.txt:hidden", "report.txt_hidden")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("AUX", "_AUX")]
    [InlineData("COM1.tar.gz", "_COM1.tar.gz")]
    [InlineData("name.   ", "name")]
    [InlineData("...", "file")]
    [InlineData("", "file")]
    [InlineData("///", "file")]
    [InlineData("tab\there.txt", "tab_here.txt")]
    public void Sanitize(string input, string expected) => Assert.Equal(expected, FileNames.Sanitize(input));

    [Fact]
    public void Long_names_keep_their_extension()
    {
        var name = FileNames.Sanitize(new string('a', 300) + ".pdf");
        Assert.Equal(FileNames.MaxLength, name.Length);
        Assert.EndsWith(".pdf", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Taken_names_get_a_number()
    {
        var taken = new HashSet<string> { "photo.jpg", "photo (2).jpg" };
        Assert.Equal("photo (3).jpg", FileNames.MakeUnique("photo.jpg", taken.Contains));
        Assert.Equal("notes", FileNames.MakeUnique("notes", taken.Contains));
    }
}

public sealed class SharedFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-files-" + Guid.NewGuid().ToString("N"));
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
    private long _free = long.MaxValue / 2;
    private readonly SharedFolder _folder;

    public SharedFolderTests() => _folder = new SharedFolder(Shared, Partial, _time, _ => _free);

    private string Shared => Path.Combine(_root, "shared");

    private string Partial => Path.Combine(_root, "partial");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] Bytes(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 7))];

    private SharedFileInfo Upload(string name, byte[] data, int chunk = 1000)
    {
        var (id, offset) = _folder.Begin(new FileUploadBeginPayload(name, data.Length, Sha(data)));
        while (offset < data.Length)
        {
            var part = data[(int)offset..(int)Math.Min(data.Length, offset + chunk)];
            offset = _folder.Chunk(new FileUploadChunkPayload(id, offset, part));
        }
        return _folder.Finish(id);
    }

    [Fact]
    public void Upload_in_chunks_arrives_intact_and_is_listed()
    {
        var data = Bytes(5500);
        string? received = null;
        _folder.Received += (_, path) => received = path;

        var info = Upload("photo.jpg", data);

        Assert.Equal("photo.jpg", info.Name);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(Shared, "photo.jpg")));
        Assert.Equal(Path.Combine(Shared, "photo.jpg"), received);
        Assert.Empty(Directory.GetFiles(Partial));
        Assert.Equal(["photo.jpg"], _folder.List().Select(f => f.Name));
    }

    [Fact]
    public void Same_name_twice_does_not_overwrite()
    {
        Upload("photo.jpg", Bytes(10));
        Assert.Equal("photo (2).jpg", Upload("photo.jpg", Bytes(20)).Name);
        Assert.Equal(10, new FileInfo(Path.Combine(Shared, "photo.jpg")).Length);
    }

    [Fact]
    public void Name_with_a_path_lands_in_the_shared_folder_only()
    {
        Assert.Equal("evil.exe", Upload(@"..\..\Startup\evil.exe", Bytes(10)).Name);
        Assert.True(File.Exists(Path.Combine(Shared, "evil.exe")));
        Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Interrupted_upload_resumes_where_it_stopped()
    {
        var data = Bytes(3000);
        var (first, _) = _folder.Begin(new FileUploadBeginPayload("video.mp4", data.Length, Sha(data)));
        _folder.Chunk(new FileUploadChunkPayload(first, 0, data[..1200]));

        // Connection lost; the phone starts again with the same file.
        var (second, offset) = _folder.Begin(new FileUploadBeginPayload("video.mp4", data.Length, Sha(data)));
        Assert.Equal(1200, offset);
        _folder.Chunk(new FileUploadChunkPayload(second, 1200, data[1200..]));
        _folder.Finish(second);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(Shared, "video.mp4")));
    }

    [Fact]
    public void Wrong_offset_and_too_much_data_are_refused()
    {
        var data = Bytes(100);
        var (id, _) = _folder.Begin(new FileUploadBeginPayload("a.bin", data.Length, Sha(data)));
        Assert.Equal(ErrorCodes.FilesOffsetMismatch, Assert.Throws<SharedFolderException>(() => _folder.Chunk(new FileUploadChunkPayload(id, 50, data[..10]))).Code);
        Assert.Equal(ErrorCodes.InvalidPayload, Assert.Throws<SharedFolderException>(() => _folder.Chunk(new FileUploadChunkPayload(id, 0, Bytes(101)))).Code);
        Assert.Equal(ErrorCodes.FilesOffsetMismatch, Assert.Throws<SharedFolderException>(() => _folder.Finish(id)).Code);
    }

    [Fact]
    public void Corrupted_upload_is_discarded()
    {
        var data = Bytes(100);
        var (id, _) = _folder.Begin(new FileUploadBeginPayload("a.bin", data.Length, Sha(Bytes(99).Append((byte)1).ToArray())));
        _folder.Chunk(new FileUploadChunkPayload(id, 0, data));
        Assert.Equal(ErrorCodes.FilesHashMismatch, Assert.Throws<SharedFolderException>(() => _folder.Finish(id)).Code);
        Assert.Empty(Directory.GetFiles(Shared));
        Assert.Empty(Directory.GetFiles(Partial));
    }

    [Fact]
    public void Cancel_removes_the_partial_file()
    {
        var data = Bytes(100);
        var (id, _) = _folder.Begin(new FileUploadBeginPayload("a.bin", data.Length, Sha(data)));
        _folder.Chunk(new FileUploadChunkPayload(id, 0, data[..10]));
        _folder.Cancel(id);
        Assert.Empty(Directory.GetFiles(Partial));
        Assert.Equal(ErrorCodes.FilesUploadNotFound, Assert.Throws<SharedFolderException>(() => _folder.Finish(id)).Code);
    }

    [Fact]
    public void Not_enough_disk_space_is_refused_up_front()
    {
        _free = SharedFolder.SpaceReserveBytes + 50;
        Assert.Equal(ErrorCodes.FilesNoSpace, Assert.Throws<SharedFolderException>(() => _folder.Begin(new FileUploadBeginPayload("big.iso", 100, new string('a', 64)))).Code);
    }

    [Fact]
    public void Download_reads_parts_and_hash_matches()
    {
        var data = Bytes(2500);
        File.WriteAllBytes(Path.Combine(Shared, "report.pdf"), data);
        var (first, size) = _folder.Read(new FileReadPayload("report.pdf", 0, 1000));
        var (last, _) = _folder.Read(new FileReadPayload("report.pdf", 2000, 1000));
        Assert.Equal(2500, size);
        Assert.Equal(data[..1000], first);
        Assert.Equal(data[2000..], last);
        Assert.Equal(Sha(data), _folder.Hash("report.pdf").Sha256);
    }

    [Fact]
    public void Only_plain_files_in_the_folder_can_be_read()
    {
        Directory.CreateDirectory(Path.Combine(Shared, "sub"));
        File.WriteAllText(Path.Combine(Shared, "sub", "inner.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "outside.txt"), "secret");
        File.WriteAllText(Path.Combine(Shared, "visible.txt"), "ok");

        Assert.Equal(["visible.txt"], _folder.List().Select(f => f.Name));
        foreach (var name in new[] { "sub", "inner.txt", "../outside.txt", "missing.txt", "VISIBLE.TXT" })
        {
            Assert.Equal(ErrorCodes.FilesNotFound, Assert.Throws<SharedFolderException>(() => _folder.Read(new FileReadPayload(name, 0, 10))).Code);
        }
    }

    [Fact]
    public void Symbolic_links_are_never_followed()
    {
        File.WriteAllText(Path.Combine(_root, "outside.txt"), "secret");
        try
        {
            File.CreateSymbolicLink(Path.Combine(Shared, "link.txt"), Path.Combine(_root, "outside.txt"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // creating links needs a privilege on Windows
        }
        Assert.Empty(_folder.List());
        Assert.Throws<SharedFolderException>(() => _folder.Read(new FileReadPayload("link.txt", 0, 10)));
    }

    [Fact]
    public void Stale_partial_uploads_are_cleaned_on_start()
    {
        var old = Path.Combine(Partial, new string('b', 64) + ".part");
        File.WriteAllBytes(old, [1, 2, 3]);
        File.SetLastWriteTimeUtc(old, _time.Now.UtcDateTime - TimeSpan.FromDays(8));
        var fresh = Path.Combine(Partial, new string('c', 64) + ".part");
        File.WriteAllBytes(fresh, [1]);
        File.SetLastWriteTimeUtc(fresh, _time.Now.UtcDateTime - TimeSpan.FromDays(1));

        _ = new SharedFolder(Shared, Partial, _time, _ => _free);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }
}

public sealed class FileParserTests
{
    private static string? Error(string type, string json)
    {
        Assert.True(CommandCatalog.V1.TryGet(type, out var spec));
        return spec.ParsePayload(JsonElement.Parse(json)).Error;
    }

    [Theory]
    [InlineData("files.upload.begin", """{"name":"a.jpg","size":-1,"sha256":"%"}""")]
    [InlineData("files.upload.begin", """{"name":"a.jpg","size":10,"sha256":"ABCDEF"}""")]
    [InlineData("files.upload.begin", """{"name":"","size":10,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}""")]
    [InlineData("files.upload.chunk", """{"uploadId":"x","offset":0,"data":"AAAA"}""")]
    [InlineData("files.upload.chunk", """{"uploadId":"6f3c3f0e-3d0e-4c4e-9a43-6f0f2b1c9d11","offset":-5,"data":"AAAA"}""")]
    [InlineData("files.upload.chunk", """{"uploadId":"6f3c3f0e-3d0e-4c4e-9a43-6f0f2b1c9d11","offset":0,"data":"***"}""")]
    [InlineData("files.read", """{"name":"../secret.txt","offset":0,"length":10}""")]
    [InlineData("files.read", """{"name":"C:secret.txt","offset":0,"length":10}""")]
    [InlineData("files.read", """{"name":"a.txt","offset":0,"length":524289}""")]
    [InlineData("files.hash", """{"name":".."}""")]
    [InlineData("clipboard.set", """{"text":""}""")]
    public void Rejects(string type, string json) => Assert.NotNull(Error(type, json));

    [Fact]
    public void A_full_size_chunk_is_accepted()
    {
        var json = JsonSerializer.Serialize(new { uploadId = Guid.NewGuid().ToString("D"), offset = 0, data = Convert.ToBase64String(new byte[SharedFolder.MaxChunkBytes]) });
        Assert.Null(Error("files.upload.chunk", json));
        var tooBig = JsonSerializer.Serialize(new { uploadId = Guid.NewGuid().ToString("D"), offset = 0, data = Convert.ToBase64String(new byte[SharedFolder.MaxChunkBytes + 1]) });
        Assert.NotNull(Error("files.upload.chunk", tooBig));
    }
}

public sealed class TrayClipboardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-clip-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDesktop _desktop = new();
    private readonly TrayCommandHandler _handler;
    private readonly List<string> _notices = [];

    public TrayClipboardTests()
    {
        _handler = new TrayCommandHandler(new UserAppStore(_dir), _desktop);
        _handler.ActionPerformed += (_, text) => _notices.Add(text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Set_then_get_and_the_pc_owner_sees_both()
    {
        Assert.True((await _handler.HandleAsync("clipboard.set", new JsonObject { ["text"] = "Привет с iPhone" }, default)).Ok);
        Assert.Equal("Привет с iPhone", _desktop.Clipboard);
        var get = await _handler.HandleAsync("clipboard.get", [], default);
        Assert.Equal("Привет с iPhone", get.Result!["text"]!.GetValue<string>());
        Assert.Equal(2, _notices.Count);
    }

    [Fact]
    public async Task Clipboard_without_text_returns_null()
    {
        var get = await _handler.HandleAsync("clipboard.get", [], default);
        Assert.True(get.Ok);
        Assert.Null(get.Result!["text"]);
    }

    [Fact]
    public async Task Files_without_a_shared_folder_fail_cleanly()
    {
        var result = await _handler.HandleAsync("files.list", [], default);
        Assert.Equal(ErrorCodes.FilesFailed, result.ErrorCode);
    }
}

public sealed class FileProtocolTests : IAsyncLifetime
{
    private AgentTestHost _host = null!;

    public async Task InitializeAsync() => _host = await AgentTestHost.StartAsync();

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        AgentTestHost.DeleteDirectory(_host.DataDirectory);
    }

    [Fact]
    public async Task Phone_sends_a_file_and_downloads_it_back_through_the_agent()
    {
        await using var tray = await _host.ConnectTrayAsync();
        var (client, deviceId, key) = await _host.PairedClientAsync();
        await using var _ = client;
        using var __ = key;

        Assert.Equal(ErrorCodes.PermissionDenied, (await client.RequestAsync("files.list")).ErrorCode());
        Assert.Equal(ErrorCodes.PermissionDenied, (await client.RequestAsync("clipboard.get")).ErrorCode());
        _host.Services.Devices.SetPermissions(deviceId, Nexus.Core.Security.Permissions.DefaultAfterPairing | Nexus.Core.Security.Permission.Files | Nexus.Core.Security.Permission.Clipboard);

        // 1.2 MB: three chunks, the first two full size (exercises the 1 MiB message limit).
        var data = new byte[1_200_000];
        new Random(7).NextBytes(data);
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        var begin = await client.RequestAsync("files.upload.begin", new JsonObject { ["name"] = "video.mp4", ["size"] = data.Length, ["sha256"] = sha });
        Assert.True(begin.IsOk(), begin.ToJsonString());
        var id = begin["payload"]!["uploadId"]!.GetValue<string>();
        long offset = 0;
        while (offset < data.Length)
        {
            var part = data[(int)offset..(int)Math.Min(data.Length, offset + SharedFolder.MaxChunkBytes)];
            var chunk = await client.RequestAsync("files.upload.chunk", new JsonObject { ["uploadId"] = id, ["offset"] = offset, ["data"] = Convert.ToBase64String(part) });
            Assert.True(chunk.IsOk(), chunk.ToJsonString());
            offset = chunk["payload"]!["received"]!.GetValue<long>();
        }
        var finish = await client.RequestAsync("files.upload.finish", new JsonObject { ["uploadId"] = id });
        Assert.True(finish.IsOk(), finish.ToJsonString());
        Assert.Equal("video.mp4", finish["payload"]!["name"]!.GetValue<string>());

        var list = await client.RequestAsync("files.list");
        Assert.Equal("video.mp4", list["payload"]!["files"]![0]!["name"]!.GetValue<string>());

        var back = new List<byte>();
        var eof = false;
        while (!eof)
        {
            var read = await client.RequestAsync("files.read", new JsonObject { ["name"] = "video.mp4", ["offset"] = back.Count, ["length"] = SharedFolder.MaxChunkBytes });
            Assert.True(read.IsOk(), read.ToJsonString()[..Math.Min(300, read.ToJsonString().Length)]);
            back.AddRange(Convert.FromBase64String(read["payload"]!["data"]!.GetValue<string>()));
            eof = read["payload"]!["eof"]!.GetValue<bool>();
        }
        Assert.Equal(data, back.ToArray());
        var hash = await client.RequestAsync("files.hash", new JsonObject { ["name"] = "video.mp4" });
        Assert.Equal(sha, hash["payload"]!["sha256"]!.GetValue<string>());

        Assert.True((await client.RequestAsync("clipboard.set", new JsonObject { ["text"] = "ссылка" })).IsOk());
        Assert.Equal("ссылка", (await client.RequestAsync("clipboard.get"))["payload"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Large_messages_are_refused_before_authentication()
    {
        await using var client = await _host.ConnectAsync();
        var response = await client.RequestAsync("system.hello", new JsonObject { ["padding"] = new string('x', 70_000) });
        Assert.Equal(ErrorCodes.TooLarge, response.ErrorCode());
    }
}
