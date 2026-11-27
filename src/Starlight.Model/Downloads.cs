using System.Net.Http.Headers;
using System.Security.Cryptography;
using Serilog;
using SharpCompress.Readers;

namespace Starlight.Model;

public enum DownloadKind { File, TarBz2 }

/// <summary>One file the first start fetches. Target is relative to the models folder.</summary>
/// <param name="Sha256">Checked after the download. Empty only until it is confirmed on the PC (PROGRESS.md).</param>
public sealed record DownloadItem(string Id, string Title, Uri Url, string Sha256, long ApproxBytes, DownloadKind Kind, string Target, bool Required);

/// <summary>What the first start can download (plan 5.13). Versions are pinned by URL and hash.</summary>
public static class DownloadManifest
{
    public static readonly DownloadItem Whisper = new("whisper", "Speech recognition (whisper large-v3-turbo)",
        new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin"), "", 574_000_000, DownloadKind.File,
        "ggml-large-v3-turbo-q5_0.bin", true);

    public static readonly DownloadItem Vad = new("vad", "Voice activity detection (Silero VAD)",
        new Uri("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx"), "", 2_300_000, DownloadKind.File,
        "voice/silero_vad.onnx", true);

    public static readonly DownloadItem Kokoro = new("kokoro", "Starlight's voice (Kokoro)",
        new Uri("https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_0.tar.bz2"), "", 333_000_000, DownloadKind.TarBz2,
        "voice/kokoro", true);

    public static readonly DownloadItem Wake = new("kws", "Wake phrase (keyword spotting)",
        new Uri("https://github.com/k2-fsa/sherpa-onnx/releases/download/kws-models/sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01.tar.bz2"), "", 19_000_000, DownloadKind.TarBz2,
        "voice/kws", true);

    public static IReadOnlyList<DownloadItem> Voice { get; } = [Whisper, Vad, Kokoro, Wake];
}

/// <summary>Downloads with resume (HTTP Range into a .part file), a SHA-256 check, and tar.bz2 unpacking.</summary>
public sealed class Downloader(HttpClient http)
{
    public async Task<string> FetchAsync(DownloadItem item, string modelsDir, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        var target = Path.GetFullPath(Path.Combine(modelsDir, item.Target));
        if (item.Kind == DownloadKind.File && File.Exists(target))
        {
            return target;
        }
        if (item.Kind == DownloadKind.TarBz2 && Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            return target;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = (item.Kind == DownloadKind.File ? target : target + ".tar.bz2") + ".part";
        await DownloadAsync(item, part, progress, ct);
        if (item.Sha256.Length > 0)
        {
            await using var check = File.OpenRead(part);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(check, ct));
            if (!hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                check.Close();
                File.Delete(part);
                throw new InvalidDataException($"{item.Title}: the download is damaged (checksum does not match). Try again.");
            }
        }
        else
        {
            Log.Warning("No checksum known yet for {Item}; download not verified", item.Id);
        }
        if (item.Kind == DownloadKind.File)
        {
            File.Move(part, target, overwrite: true);
        }
        else
        {
            var temp = target + ".unpack";
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, true);
            }
            try
            {
                await using (var archive = File.OpenRead(part))
                {
                    Unpack(archive, temp);
                }
            }
            catch
            {
                // A broken archive must not stay behind: the next try downloads it fresh.
                TryDelete(() => Directory.Delete(temp, true));
                TryDelete(() => File.Delete(part));
                throw;
            }
            // Swap: the old folder is kept until the new one is in place.
            var old = target + ".old";
            if (Directory.Exists(old))
            {
                Directory.Delete(old, true);
            }
            if (Directory.Exists(target))
            {
                Directory.Move(target, old);
            }
            Directory.Move(temp, target);
            TryDelete(() => Directory.Delete(old, true));
            File.Delete(part);
        }
        Log.Information("Downloaded {Item} to {Target}", item.Id, target);
        return target;
    }

    private async Task DownloadAsync(DownloadItem item, string part, IProgress<(long, long)>? progress, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var have = File.Exists(part) ? new FileInfo(part).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
            if (have > 0)
            {
                request.Headers.Range = new RangeHeaderValue(have, null);
            }
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // Complete only if the server says the file has exactly the bytes we have ("bytes */total").
                    if (response.Content.Headers.ContentRange?.Length == have)
                    {
                        return;
                    }
                    Log.Warning("Download of {Item}: the partial file does not fit the server's file; starting over", item.Id);
                    File.Delete(part);
                    if (attempt >= 4)
                    {
                        throw new IOException($"{item.Title}: the server refused to resume the download.");
                    }
                    continue;
                }
                response.EnsureSuccessStatusCode();
                var resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (!resumed)
                {
                    have = 0;
                }
                var total = have + (response.Content.Headers.ContentLength ?? item.ApproxBytes - have);
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                var buffer = new byte[1 << 16];
                int n;
                var done = have;
                var lastReport = 0L;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (done - lastReport > 1 << 20)
                    {
                        progress?.Report((done, total));
                        lastReport = done;
                    }
                }
                progress?.Report((done, total));
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && attempt < 4 && !ct.IsCancellationRequested)
            {
                Log.Warning(ex, "Download of {Item} broke off (try {Attempt}); resuming", item.Id, attempt);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not clean up after a download");
        }
    }

    /// <summary>Unpacks a tar.bz2 into a folder, dropping the archive's single top folder. Paths that would leave
    /// the folder are refused.</summary>
    internal static void Unpack(Stream archive, string dir)
    {
        Directory.CreateDirectory(dir);
        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        using var reader = ReaderFactory.OpenReader(archive, new ReaderOptions());
        while (reader.MoveToNextEntry())
        {
            var key = reader.Entry.Key?.Replace('\\', '/').TrimStart('/') ?? "";
            if (key.Length == 0 || key.StartsWith("./", StringComparison.Ordinal) && key.Length == 2)
            {
                continue;
            }
            var parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // Drop the top folder (kokoro-multi-lang-v1_0/...).
            var rel = parts.Length > 1 ? string.Join('/', parts.Skip(1)) : parts[0];
            if (parts.Length == 1 && reader.Entry.IsDirectory)
            {
                continue;
            }
            var path = Path.GetFullPath(Path.Combine(dir, rel));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The archive has a bad path: {key}");
            }
            if (reader.Entry.IsDirectory)
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = File.Create(path);
            reader.WriteEntryTo(output);
        }
    }
}
