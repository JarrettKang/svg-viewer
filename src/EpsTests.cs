using System.Buffers.Binary;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace SvgViewer;

internal sealed partial class ViewerForm
{
    private async Task RunEpsTestsAsync(string root, Action<bool, string> check, Func<string, Task<bool>> eval, Func<string, Task> wait)
    {
        string? gs = await Task.Run(GhostscriptLocator.Find);
        check(gs != null, "自动检测本机 Ghostscript（EPS 集成测试需要已安装）");
        string version = await GhostscriptLocator.CheckVersionAsync(gs!);
        check(!string.IsNullOrEmpty(version), "Ghostscript 命令行版本验证");
        string folder = Path.Combine(root, "EPS 空格 & 路径");
        Directory.CreateDirectory(folder);
        string svg = Path.Combine(folder, "图1.svg"), eps = Path.Combine(folder, "图2.EPS");
        File.Copy(Path.Combine(root, "fixtures", "图片1.svg"), svg, true);
        string source = "%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 10 20 410 260\n%%EndComments\n0.2 0.55 0.85 setrgbcolor\n30 40 360 200 rectfill\n%%EOF\n";
        File.WriteAllText(eps, source, Encoding.ASCII);
        string cache = Path.Combine(root, "eps-cache-" + Guid.NewGuid().ToString("N"));
        var preview = await EpsRenderer.RenderAsync(eps, gs!, cacheRoot: cache);
        using (var memory = new MemoryStream(preview.Data))
        using (var bitmap = new Bitmap(memory))
        {
            check(bitmap.Width == 1600 && bitmap.Height == 960, "EPS 非零 BoundingBox 裁切与 288 DPI 渲染");
            check(bitmap.GetPixel(0, 0).A == 0 && bitmap.GetPixel(800, 480).B > bitmap.GetPixel(800, 480).R, "EPS 透明背景与实际图形像素正确");
        }
        check(!preview.FromCache, "首次转换 EPS（无 showpage 的 EPS 也可显示）");
        var cached = await EpsRenderer.RenderAsync(eps, gs!, cacheRoot: cache);
        check(cached.FromCache && preview.Data.SequenceEqual(cached.Data), "重复打开复用 EPS 预览缓存");
        DateTime oldStamp = File.GetLastWriteTimeUtc(eps);
        File.WriteAllText(eps, source.Replace("0.2 0.55 0.85", "0.85 0.25 0.2"), Encoding.ASCII);
        File.SetLastWriteTimeUtc(eps, oldStamp);
        var updated = await EpsRenderer.RenderAsync(eps, gs!, cacheRoot: cache);
        check(!updated.FromCache && !preview.Data.SequenceEqual(updated.Data), "EPS 内容变化后失效缓存（即使时间戳相同）");
        var catalog = FileCatalog.Scan(eps);
        check(catalog.Count == 2 && catalog[0] == svg && catalog[1] == eps, "SVG / EPS 混合自然排序和大写扩展名");
        await OpenAsync(eps);
        await wait("picture.alt==='图2.EPS' && state.index===2");
        check(await eval("picture.src.startsWith('data:image/png') && $('dimensions').textContent.includes('EPS 预览') && Math.abs(state.width-400*96/72)<.01"), "EPS 预览接入原有画布并保留逻辑尺寸");
        check(await eval("(()=>{let before=state.scale;zoomAt(1.2);return state.scale>before})()"), "EPS 可使用原有缩放操作");
        await web.CoreWebView2.ExecuteScriptAsync("fit()");
        using (var screenshot = File.Create(Path.Combine(root, "eps-viewer.png")))
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot);
        await NavigateAsync(-1);
        await wait("picture.alt==='图1.svg' && state.index===1");
        check(currentPath == svg, "EPS 上一张切换到 SVG");
        await NavigateAsync(1);
        await wait("picture.alt==='图2.EPS' && state.index===2");
        check(currentPath == eps && await eval("state.fit"), "SVG 下一张切换到 EPS 并适应窗口");
        string? oldOverride = Environment.GetEnvironmentVariable("SVGVIEWER_GHOSTSCRIPT");
        try
        {
            Environment.SetEnvironmentVariable("SVGVIEWER_GHOSTSCRIPT", Path.Combine(root, "not-installed", "gswin64c.exe"));
            await OpenAsync(eps);
            await wait("$('ghostscriptDialog').open");
            check(await eval("$('loading').style.display==='none' && $('gsStatus').textContent.includes('需要安装')"), "缺少 Ghostscript 时提示安装或选择路径");
            await web.CoreWebView2.ExecuteScriptAsync("$('ghostscriptDialog').close()");
            await OpenAsync(svg);
            await wait("picture.alt==='图1.svg'");
            check(currentPath == svg, "未安装 Ghostscript 时 SVG 仍然正常查看");
        }
        finally { Environment.SetEnvironmentVariable("SVGVIEWER_GHOSTSCRIPT", oldOverride); }
        string invalid = Path.Combine(root, "bad.eps");
        File.WriteAllText(invalid, source + "\nThisIsNotPostScript\n", Encoding.ASCII);
        await OpenAsync(invalid);
        await wait("$('toast').textContent.includes('bad.eps')");
        check(currentPath == svg, "EPS 转换错误保留上一张有效图片");
        string atEnd = source.Replace("%%BoundingBox: 10 20 410 260", "%%BoundingBox: (atend)") + "\n%%Trailer\n%%BoundingBox: 10 20 410 260\n";
        check(EpsRenderer.Measure(Encoding.ASCII.GetBytes(atEnd)) == (400, 240), "支持 EPS 尾部 BoundingBox (atend)");
        byte[] ps = Encoding.ASCII.GetBytes(source), dos = new byte[30 + ps.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(dos, 0xc6d3d0c5);
        BinaryPrimitives.WriteUInt32LittleEndian(dos.AsSpan(4), 30);
        BinaryPrimitives.WriteUInt32LittleEndian(dos.AsSpan(8), (uint)ps.Length);
        ps.CopyTo(dos, 30);
        string binary = Path.Combine(root, "binary.eps");
        await File.WriteAllBytesAsync(binary, dos);
        var binaryPreview = await EpsRenderer.RenderAsync(binary, gs!, cacheRoot: cache);
        check(binaryPreview.Data.SequenceEqual(preview.Data), "支持包含二进制预览头的 DOS EPS");
        string loop = Path.Combine(root, "loop.eps");
        File.WriteAllText(loop, "%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 100 100\n{} loop\n", Encoding.ASCII);
        bool timedOut = false;
        try { await EpsRenderer.RenderAsync(loop, gs!, cacheRoot: cache, timeout: TimeSpan.FromMilliseconds(200)); }
        catch (IOException ex) { timedOut = ex.Message.Contains("已停止"); }
        check(timedOut, "EPS 超时后停止 Ghostscript 子进程");
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
        {
            bool cancelled = false;
            try { await EpsRenderer.RenderAsync(loop, gs!, cancel.Token, cache); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "EPS 转换支持取消");
        }
        var slow = OpenAsync(loop);
        await Task.Delay(150);
        await OpenAsync(svg);
        await slow;
        await wait("picture.alt==='图1.svg' && $('loading').style.display==='none'");
        check(currentPath == svg, "切图时取消旧 EPS 转换且不覆盖新图片");
        check(Directory.GetDirectories(cache, "job-*").Length == 0, "转换成功、错误和取消后清理临时文件");
    }
}
