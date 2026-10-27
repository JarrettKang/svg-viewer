using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace SvgViewer;

internal sealed partial class ViewerForm : Form
{
    private readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(22, 24, 29) };
    private readonly string? initialPath;
    private readonly string? testOutput;
    private string? currentPath;
    private List<string> files = [];
    private bool fullscreen;
    private Rectangle previousBounds;
    private FormWindowState previousState;
    private int openGeneration;
    private CancellationTokenSource? openCancellation;
    private string? failedEps;

    public ViewerForm(string? path, string? testOutput)
    {
        initialPath = path;
        this.testOutput = testOutput;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "SVG 查看器";
        MinimumSize = new Size(720, 480);
        StartPosition = FormStartPosition.Manual;
        Controls.Add(web);
        Shown += async (_, _) => await InitializeAsync();
    }

    protected override void OnLoad(EventArgs e)
    {
        // Use actual working-area pixels after WinForms has applied DPI scaling.
        // Restore remains roomy; the initial view uses all available desktop space.
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        MinimumSize = new Size(Math.Min(720, area.Width), Math.Min(480, area.Height));
        int width = (int)(area.Width * .92);
        int height = (int)(area.Height * .92);
        Bounds = new Rectangle(area.Left + (area.Width - width) / 2,
            area.Top + (area.Height - height) / 2, width, height);
        WindowState = FormWindowState.Maximized;
        base.OnLoad(e);
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (testOutput != null) File.AppendAllText(Path.Combine(testOutput, "startup.log"), "Form shown\n");
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SvgViewer", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: cache);
            await web.EnsureCoreWebView2Async(env);
            if (testOutput != null) File.AppendAllText(Path.Combine(testOutput, "startup.log"), "WebView2 initialized\n");
            var core = web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            bool initialNavigation = true;
            core.NavigationStarting += (_, e) =>
            {
                // NavigateToString reports a data: URI here even though the document's origin is about:blank.
                if (initialNavigation && e.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
                    initialNavigation = false;
                else e.Cancel = true;
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (testOutput != null)
                {
                    File.AppendAllText(Path.Combine(testOutput, "startup.log"), "Navigation completed: " + e.IsSuccess + " / " + e.WebErrorStatus + "\n");
                }
            };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += OnMessage;
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SvgViewer.viewer.html")!;
            using var reader = new StreamReader(stream);
            core.NavigateToString(await reader.ReadToEndAsync());
            if (testOutput != null) File.AppendAllText(Path.Combine(testOutput, "startup.log"), "HTML navigation requested\n");
        }
        catch (Exception ex)
        {
            if (testOutput != null) { WriteTestFailure(ex); Close(); return; }
            MessageBox.Show(this, "启动失败：" + ex.Message + "\n\n请确认已安装 Microsoft Edge WebView2 Runtime。", "SVG 查看器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (testOutput != null) File.AppendAllText(Path.Combine(testOutput, "startup.log"), "Message: " + e.Source + " " + e.WebMessageAsJson + "\n");
        if (e.Source != "about:blank") return;
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            string? action = message.RootElement.GetProperty("action").GetString();
            switch (action)
            {
                case "ready":
                    if (testOutput != null) { await RunSelfTestsAsync(); return; }
                    await ReportGhostscriptAsync();
                    if (!string.IsNullOrWhiteSpace(initialPath)) await OpenAsync(initialPath);
                    break;
                case "open":
                    using (var dialog = new OpenFileDialog { Filter = "矢量图片 (*.svg;*.eps)|*.svg;*.eps|SVG 图片 (*.svg)|*.svg|EPS 图片 (*.eps)|*.eps", Title = "打开图片", CheckFileExists = true })
                        if (dialog.ShowDialog(this) == DialogResult.OK) await OpenAsync(dialog.FileName);
                    break;
                case "drop":
                    var file = e.AdditionalObjects?.OfType<CoreWebView2File>().FirstOrDefault();
                    if (file != null) await OpenAsync(file.Path);
                    break;
                case "previous": await NavigateAsync(-1); break;
                case "next": await NavigateAsync(1); break;
                case "reload": if (currentPath != null) await OpenAsync(currentPath); break;
                case "reveal":
                    if (currentPath != null) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{currentPath}\"") { UseShellExecute = true });
                    break;
                case "fullscreen": ToggleFullscreen(); break;
                case "exitFullscreen": if (fullscreen) ToggleFullscreen(); break;
                case "associate": RegisterAssociation(); break;
                case "chooseGhostscript": await ChooseGhostscriptAsync(); break;
                case "downloadGhostscript": Process.Start(new ProcessStartInfo("https://ghostscript.com/releases/gsdnld.html") { UseShellExecute = true }); break;
            }
        }
        catch (Exception ex) { Send(new { type = "error", message = ex.Message }); }
    }

    internal async Task OpenAsync(string path)
    {
        int generation = ++openGeneration;
        openCancellation?.Cancel();
        openCancellation?.Dispose();
        openCancellation = new CancellationTokenSource();
        var cancellation = openCancellation.Token;
        path = Path.GetFullPath(path);
        try
        {
            if (!FileCatalog.IsSupported(path)) throw new InvalidDataException("请选择 .svg 或 .eps 文件。");
            bool eps = Path.GetExtension(path).Equals(".eps", StringComparison.OrdinalIgnoreCase);
            byte[] bytes;
            double? width = null, height = null;
            if (eps)
            {
                failedEps = path;
                Send(new { type = "loading", message = "正在转换 EPS…" });
                string? gs = await Task.Run(GhostscriptLocator.Find, cancellation);
                if (generation != openGeneration) return;
                if (gs == null)
                {
                    Send(new { type = "needGhostscript", message = "查看 EPS 需要安装 Ghostscript，或选择已有的 gswin64c.exe。SVG 查看不受影响。" });
                    return;
                }
                var preview = await EpsRenderer.RenderAsync(path, gs, cancellation);
                bytes = preview.Data;
                width = preview.Width;
                height = preview.Height;
            }
            else bytes = await Task.Run(() => FileCatalog.ReadSvg(path), cancellation);
            List<string> catalog;
            try { catalog = await Task.Run(() => FileCatalog.Scan(path)); }
            catch (IOException) { catalog = [path]; }
            catch (UnauthorizedAccessException) { catalog = [path]; }
            if (generation != openGeneration) return;
            files = catalog;
            currentPath = path;
            failedEps = null;
            int index = files.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (index < 0) { files.Add(path); index = files.Count - 1; }
            Text = $"{Path.GetFileName(path)} — SVG 查看器";
            Send(new { type = "image", data = Convert.ToBase64String(bytes), mime = eps ? "image/png" : "image/svg+xml", width, height,
                format = eps ? "EPS 预览" : "SVG", name = Path.GetFileName(path), path, index = index + 1, total = files.Count });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == openGeneration) Send(new { type = "error", message = $"无法打开 {Path.GetFileName(path)}：{ex.Message}" }); }
    }

    internal async Task NavigateAsync(int offset)
    {
        if (currentPath == null) return;
        try { files = await Task.Run(() => FileCatalog.Scan(currentPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Send(new { type = "error", message = "无法读取文件夹：" + ex.Message }); return; }
        if (files.Count == 0) { Send(new { type = "error", message = "文件夹里已没有 SVG / EPS 图片。" }); return; }
        int index = files.FindIndex(p => string.Equals(p, currentPath, StringComparison.OrdinalIgnoreCase));
        int next = index < 0 ? (offset > 0 ? 0 : files.Count - 1) : index + offset;
        if (next < 0 || next >= files.Count) return;
        await OpenAsync(files[next]);
    }

    private void Send(object data)
    {
        if (!IsDisposed && !Disposing && !web.IsDisposed && web.CoreWebView2 != null)
            web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(data));
    }

    private async Task ReportGhostscriptAsync()
    {
        string? path = await Task.Run(GhostscriptLocator.Find);
        Send(new { type = "ghostscript", message = path == null ? "Ghostscript：未检测到（仅查看 EPS 时需要）" : "Ghostscript：" + path });
    }

    private async Task ChooseGhostscriptAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "Ghostscript 命令行程序|gswin64c.exe;gswin32c.exe;gs.exe", Title = "选择 Ghostscript 命令行程序", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string version = await GhostscriptLocator.CheckVersionAsync(dialog.FileName);
        GhostscriptLocator.Save(dialog.FileName);
        Send(new { type = "ghostscriptSelected", message = $"Ghostscript {version}：{dialog.FileName}" });
        if (failedEps != null) await OpenAsync(failedEps);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        openCancellation?.Cancel();
        base.OnFormClosed(e);
    }

    private void ToggleFullscreen()
    {
        if (!fullscreen)
        {
            previousState = WindowState;
            previousBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = previousBounds;
            WindowState = previousState;
        }
        fullscreen = !fullscreen;
        Send(new { type = "fullscreen", value = fullscreen });
    }

    private void RegisterAssociation()
    {
        string exe = Environment.ProcessPath!;
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\SvgViewer.Image"))
        {
            key.SetValue("", "SVG / EPS 图片");
            using var icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{exe}\",0");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exe}\" \"%1\"");
        }
        foreach (string extension in new[] { ".svg", ".eps" })
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids"))
            key.SetValue("SvgViewer.Image", Array.Empty<byte>(), RegistryValueKind.None);
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\SvgViewer\Capabilities"))
        {
            key.SetValue("ApplicationName", "SVG 查看器");
            key.SetValue("ApplicationDescription", "查看 SVG / EPS，滚轮缩放、拖动、同文件夹切换");
            using var associations = key.CreateSubKey("FileAssociations");
            associations.SetValue(".svg", "SvgViewer.Image");
            associations.SetValue(".eps", "SvgViewer.Image");
        }
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            key.SetValue("SVG 查看器", @"Software\SvgViewer\Capabilities");
        Send(new { type = "notice", message = "已注册 SVG 查看器。请在 Windows 默认应用中搜索 .svg 或 .eps，选择“SVG 查看器”。请保持程序所在位置不变。" });
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
    }

    private void WriteTestFailure(Exception ex)
    {
        Directory.CreateDirectory(testOutput!);
        File.WriteAllText(Path.Combine(testOutput!, "result.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
