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
                    if (!string.IsNullOrWhiteSpace(initialPath)) await OpenAsync(initialPath);
                    break;
                case "open":
                    using (var dialog = new OpenFileDialog { Filter = "SVG 图片 (*.svg)|*.svg", Title = "打开 SVG 图片", CheckFileExists = true })
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
            }
        }
        catch (Exception ex) { Send(new { type = "error", message = ex.Message }); }
    }

    internal async Task OpenAsync(string path)
    {
        int generation = ++openGeneration;
        path = Path.GetFullPath(path);
        try
        {
            var bytes = await Task.Run(() => FileCatalog.ReadSvg(path));
            List<string> catalog;
            try { catalog = await Task.Run(() => FileCatalog.Scan(path)); }
            catch (IOException) { catalog = [path]; }
            catch (UnauthorizedAccessException) { catalog = [path]; }
            if (generation != openGeneration) return;
            files = catalog;
            currentPath = path;
            int index = files.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (index < 0) { files.Add(path); index = files.Count - 1; }
            Text = $"{Path.GetFileName(path)} — SVG 查看器";
            Send(new { type = "image", data = Convert.ToBase64String(bytes), name = Path.GetFileName(path), path, index = index + 1, total = files.Count });
        }
        catch (Exception ex) { if (generation == openGeneration) Send(new { type = "error", message = $"无法打开 {Path.GetFileName(path)}：{ex.Message}" }); }
    }

    internal async Task NavigateAsync(int offset)
    {
        if (currentPath == null) return;
        try { files = await Task.Run(() => FileCatalog.Scan(currentPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Send(new { type = "error", message = "无法读取文件夹：" + ex.Message }); return; }
        if (files.Count == 0) { Send(new { type = "error", message = "文件夹里已没有 SVG 图片。" }); return; }
        int index = files.FindIndex(p => string.Equals(p, currentPath, StringComparison.OrdinalIgnoreCase));
        int next = index < 0 ? (offset > 0 ? 0 : files.Count - 1) : index + offset;
        if (next < 0 || next >= files.Count) return;
        await OpenAsync(files[next]);
    }

    private void Send(object data) => web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(data));

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
            key.SetValue("", "SVG 矢量图片");
            using var icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{exe}\",0");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exe}\" \"%1\"");
        }
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.svg\OpenWithProgids"))
            key.SetValue("SvgViewer.Image", Array.Empty<byte>(), RegistryValueKind.None);
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\SvgViewer\Capabilities"))
        {
            key.SetValue("ApplicationName", "SVG 查看器");
            key.SetValue("ApplicationDescription", "查看 SVG，滚轮缩放、拖动、同文件夹切换");
            using var associations = key.CreateSubKey("FileAssociations");
            associations.SetValue(".svg", "SvgViewer.Image");
        }
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            key.SetValue("SVG 查看器", @"Software\SvgViewer\Capabilities");
        Send(new { type = "notice", message = "已注册 SVG 查看器。请在 Windows 默认应用中搜索 .svg，选择“SVG 查看器”。请保持程序所在位置不变。" });
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
    }

    private void WriteTestFailure(Exception ex)
    {
        Directory.CreateDirectory(testOutput!);
        File.WriteAllText(Path.Combine(testOutput!, "result.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
