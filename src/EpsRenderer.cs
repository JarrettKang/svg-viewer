using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SvgViewer;

internal sealed record EpsPreview(byte[] Data, double Width, double Height, bool FromCache);

internal static class EpsRenderer
{
    private const int MaxSide = 8192;
    private const long MaxPixels = 24_000_000;
    internal static string CacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SvgViewer", "EpsCache");

    public static async Task<EpsPreview> RenderAsync(string path, string executable, CancellationToken cancellation = default, string? cacheRoot = null, TimeSpan? timeout = null)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("文件超过 64 MB，暂不支持打开。");
        byte[] source = await File.ReadAllBytesAsync(path, cancellation);
        if (source.Length > 64 * 1024 * 1024) throw new InvalidDataException("文件超过 64 MB，暂不支持打开。");
        // DOS EPS may contain TIFF/WMF previews. Extract only its PostScript section.
        if (source.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(source) == 0xc6d3d0c5)
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(4));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(8));
            if (offset < 12 || (ulong)offset + length > (ulong)source.Length) throw new InvalidDataException("EPS 二进制文件头损坏。");
            source = source.AsSpan((int)offset, (int)length).ToArray();
        }
        var (width, height) = Measure(source);
        double dpi = Math.Min(288, Math.Min(MaxSide * 72 / Math.Max(width, height), Math.Sqrt(MaxPixels / (width * height)) * 72));
        dpi = Math.Floor(dpi * 1000) / 1000;
        if (dpi < 1) throw new InvalidDataException("EPS 画布尺寸过大。");
        string root = Path.GetFullPath(cacheRoot ?? CacheRoot);
        Directory.CreateDirectory(root);
        var exe = new FileInfo(executable);
        string identity = $"eps-v1|{exe.FullName}|{exe.Length}|{exe.LastWriteTimeUtc.Ticks}|{dpi.ToString(CultureInfo.InvariantCulture)}";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(source);
        hash.AppendData(Encoding.UTF8.GetBytes(identity));
        string cached = Path.Combine(root, Convert.ToHexString(hash.GetHashAndReset()) + ".png");
        if (File.Exists(cached))
        {
            try { byte[] bytes = await File.ReadAllBytesAsync(cached, cancellation); ValidatePng(bytes); return new(bytes, width * 96 / 72, height * 96 / 72, true); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        }
        string job = Path.Combine(root, "job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        try
        {
            await GhostscriptLocator.CheckVersionAsync(executable);
            cancellation.ThrowIfCancellationRequested();
            string input = Path.Combine(job, "input.eps"), output = Path.Combine(job, "preview.png");
            await File.WriteAllBytesAsync(input, source, cancellation);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = job,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in new[] { "-dSAFER", "-dBATCH", "-dNOPAUSE", "-dEPSCrop", "-dFirstPage=1", "-dLastPage=1", "-sDEVICE=pngalpha", "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4", "-r" + dpi.ToString(CultureInfo.InvariantCulture), "-sOutputFile=" + output, "-f", input, "-c", "showpage" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("无法启动 Ghostscript。");
            var stdout = ReadLogAsync(process.StandardOutput);
            var stderr = ReadLogAsync(process.StandardError);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync();
                await Task.WhenAll(stdout, stderr);
                cancellation.ThrowIfCancellationRequested();
                throw new IOException("EPS 转换超过 30 秒，已停止。文件可能过于复杂或已损坏。");
            }
            string log = (await stdout) + (await stderr);
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new IOException("Ghostscript 无法转换此 EPS。" + (log.Contains("/undefined") ? "文件包含无法识别的 PostScript 指令。" : "请检查文件和 Ghostscript 安装。"));
            if (new FileInfo(output).Length > 100 * 1024 * 1024) throw new InvalidDataException("EPS 预览文件过大。");
            byte[] png = await File.ReadAllBytesAsync(output, cancellation);
            ValidatePng(png);
            File.Move(output, cached, true);
            TrimCache(root, cached);
            return new(png, width * 96 / 72, height * 96 / 72, false);
        }
        finally
        {
            try { Directory.Delete(job, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static (double Width, double Height) Measure(byte[] source)
    {
        string text = Encoding.Latin1.GetString(source);
        if (!text.StartsWith("%!PS", StringComparison.Ordinal)) throw new InvalidDataException("这个文件不是有效的 EPS/PostScript 图片。");
        var boxes = new Dictionary<string, (double, double)>();
        int nested = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("%%BeginDocument")) { nested++; continue; }
            if (line.StartsWith("%%EndDocument")) { nested = Math.Max(0, nested - 1); continue; }
            if (nested != 0 || (!line.StartsWith("%%BoundingBox:") && !line.StartsWith("%%HiResBoundingBox:"))) continue;
            var match = Regex.Match(line, @"^%%(HiResBoundingBox|BoundingBox):\s+([-+\d.eE]+)\s+([-+\d.eE]+)\s+([-+\d.eE]+)\s+([-+\d.eE]+)\s*$", RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var numbers = new double[4];
            bool valid = true;
            for (int i = 0; i < 4; i++) valid &= double.TryParse(match.Groups[i + 2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) && double.IsFinite(numbers[i]);
            double w = numbers[2] - numbers[0], h = numbers[3] - numbers[1];
            if (!valid || w <= 0 || h <= 0) throw new InvalidDataException("EPS BoundingBox 尺寸无效。");
            boxes.TryAdd(match.Groups[1].Value, (w, h));
        }
        if (boxes.TryGetValue("HiResBoundingBox", out var precise)) return precise;
        if (boxes.TryGetValue("BoundingBox", out var normal)) return normal;
        throw new InvalidDataException("EPS 缺少有效的 BoundingBox，无法确定图片范围。");
    }

    private static void ValidatePng(byte[] png)
    {
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("Ghostscript 没有生成有效的 PNG 预览。");
        uint width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)), height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20));
        if (width == 0 || height == 0 || width > MaxSide || height > MaxSide || (ulong)width * height > MaxPixels + MaxSide * 2)
            throw new InvalidDataException("EPS 预览尺寸超过限制。");
    }

    internal static async Task<string> ReadLogAsync(StreamReader reader)
    {
        var result = new StringBuilder();
        char[] buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
            if (result.Length < 8192) result.Append(buffer, 0, Math.Min(count, 8192 - result.Length));
        return result.ToString();
    }

    private static void TrimCache(string root, string keep)
    {
        try
        {
            var items = new DirectoryInfo(root).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            long size = 0;
            foreach (var item in items) { size += item.Length; if (size > 256L * 1024 * 1024 && item.FullName != keep) item.Delete(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
