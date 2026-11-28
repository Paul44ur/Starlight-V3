using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using SharpCompress.Common;
using SharpCompress.Writers;
using Starlight.Model;

namespace Starlight.Tests;

/// <summary>Serves one file, honours Range, and can break off after some bytes once.</summary>
internal sealed class RangeServer(byte[] body) : HttpMessageHandler
{
    public int BreakAfter { get; set; } = -1;
    public bool StallOnce { get; set; }
    public List<long?> RangesAsked { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var from = request.Headers.Range?.Ranges.First().From;
        RangesAsked.Add(from);
        var start = (int)(from ?? 0);
        if (start >= body.Length)
        {
            var refused = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
            refused.Content.Headers.ContentRange = new ContentRangeHeaderValue(body.Length);
            return Task.FromResult(refused);
        }
        var slice = body[start..];
        if (StallOnce)
        {
            StallOnce = false;
            var stalled = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) };
            stalled.Content.Headers.ContentLength = body.Length;
            return Task.FromResult(stalled);
        }
        if (BreakAfter > 0)
        {
            slice = slice[..BreakAfter];
            BreakAfter = -1;
            var broken = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BreakingStream(slice)) };
            broken.Content.Headers.ContentLength = body.Length;
            return Task.FromResult(broken);
        }
        var response = new HttpResponseMessage(from is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = new ByteArrayContent(slice) };
        if (from is not null)
        {
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, body.Length - 1, body.Length);
        }
        return Task.FromResult(response);
    }

    private sealed class StallingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }

    private sealed class BreakingStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = base.Read(buffer, offset, count);
            return n == 0 ? throw new IOException("connection reset") : n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await base.ReadAsync(buffer, ct);
            return n == 0 ? throw new IOException("connection reset") : n;
        }
    }
}

public sealed class DownloadTests
{
    private static byte[] Body(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 7)).ToArray();

    [Fact]
    public async Task A_stalled_download_counts_as_broken_and_is_tried_again()
    {
        using var env = new TestEnv();
        var body = Body(50_000);
        var server = new RangeServer(body) { StallOnce = true };
        var item = new DownloadItem("s", "S", new Uri("https://example.test/s.bin"), Convert.ToHexString(SHA256.HashData(body)), body.Length, DownloadKind.File, "s.bin", true);
        var path = await new Downloader(new HttpClient(server)) { StallTimeout = TimeSpan.FromMilliseconds(200) }
            .FetchAsync(item, env.Root, null, default).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(body, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, server.RangesAsked.Count);
    }

    [Fact]
    public async Task A_broken_download_resumes_where_it_stopped_and_is_checked()
    {
        using var env = new TestEnv();
        var body = Body(3_000_000);
        var server = new RangeServer(body) { BreakAfter = 1_000_000 };
        var item = new DownloadItem("x", "X", new Uri("https://example.test/x.bin"), Convert.ToHexString(SHA256.HashData(body)), body.Length, DownloadKind.File, "x.bin", true);

        var path = await new Downloader(new HttpClient(server)).FetchAsync(item, env.Root, null, default);

        Assert.Equal(body, await File.ReadAllBytesAsync(path));
        Assert.Equal([null, 1_000_000L], server.RangesAsked);
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task A_damaged_download_is_deleted_and_reported()
    {
        using var env = new TestEnv();
        var item = new DownloadItem("x", "X", new Uri("https://example.test/x.bin"), new string('0', 64), 100, DownloadKind.File, "x.bin", true);
        await Assert.ThrowsAsync<InvalidDataException>(() => new Downloader(new HttpClient(new RangeServer(Body(100)))).FetchAsync(item, env.Root, null, default));
        Assert.Empty(Directory.GetFiles(env.Root, "x.bin*"));
    }

    [Fact]
    public async Task An_archive_is_unpacked_without_its_top_folder()
    {
        using var env = new TestEnv();
        var archive = new MemoryStream();
        using (var writer = WriterFactory.OpenWriter(archive, ArchiveType.Tar, new WriterOptions(CompressionType.BZip2) { LeaveStreamOpen = true }))
        {
            writer.Write("kokoro-v1/model.onnx", new MemoryStream([1, 2, 3]), DateTime.Now);
            writer.Write("kokoro-v1/espeak-ng-data/en_dict", new MemoryStream([4]), DateTime.Now);
        }
        var bytes = archive.ToArray();
        var item = new DownloadItem("k", "K", new Uri("https://example.test/k.tar.bz2"), Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, DownloadKind.TarBz2, "voice/kokoro", true);

        var dir = await new Downloader(new HttpClient(new RangeServer(bytes))).FetchAsync(item, env.Root, null, default);

        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(dir, "model.onnx")));
        Assert.True(File.Exists(Path.Combine(dir, "espeak-ng-data", "en_dict")));
    }

    [Fact]
    public void An_archive_path_that_leaves_the_folder_is_refused()
    {
        using var env = new TestEnv();
        var archive = new MemoryStream();
        using (var writer = WriterFactory.OpenWriter(archive, ArchiveType.Tar, new WriterOptions(CompressionType.BZip2) { LeaveStreamOpen = true }))
        {
            writer.Write("top/../../evil.txt", new MemoryStream([1]), DateTime.Now);
        }
        archive.Position = 0;
        Assert.Throws<InvalidDataException>(() => Downloader.Unpack(archive, Path.Combine(env.Root, "out")));
        Assert.False(File.Exists(Path.Combine(env.Root, "evil.txt")));
    }

    [Fact]
    public async Task A_part_file_longer_than_the_real_file_is_downloaded_again()
    {
        using var env = new TestEnv();
        var body = Body(1000);
        var server = new RangeServer(body);
        var item = new DownloadItem("x", "X", new Uri("https://example.test/x.bin"), Convert.ToHexString(SHA256.HashData(body)), body.Length, DownloadKind.File, "x.bin", true);
        await File.WriteAllBytesAsync(Path.Combine(env.Root, "x.bin.part"), new byte[1500]);

        var path = await new Downloader(new HttpClient(server)).FetchAsync(item, env.Root, null, default);

        Assert.Equal(body, await File.ReadAllBytesAsync(path));
        Assert.Equal([1500L, null], server.RangesAsked);
    }

    [Fact]
    public async Task A_broken_archive_leaves_nothing_behind()
    {
        using var env = new TestEnv();
        var junk = Body(5000);
        var item = new DownloadItem("k", "K", new Uri("https://example.test/k.tar.bz2"), Convert.ToHexString(SHA256.HashData(junk)), junk.Length, DownloadKind.TarBz2, "voice/kokoro", true);

        await Assert.ThrowsAnyAsync<Exception>(() => new Downloader(new HttpClient(new RangeServer(junk))).FetchAsync(item, env.Root, null, default));

        var voice = Path.Combine(env.Root, "voice");
        Assert.Empty(Directory.GetFileSystemEntries(voice));
    }
}
