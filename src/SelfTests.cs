using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SvgViewer;

internal sealed partial class ViewerForm
{
    private async Task RunSelfTestsAsync()
    {
        var checks = new List<string>();
        string root = Path.GetFullPath(testOutput!);
        Directory.CreateDirectory(root);
        string fixture = Path.Combine(root, "fixtures");
        Directory.CreateDirectory(fixture);
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            checks.Add(name);
        }
        async Task<bool> Eval(string code) => await web.CoreWebView2.ExecuteScriptAsync(code) == "true";
        async Task WaitFor(string code)
        {
            for (int i = 0; i < 200; i++)
            {
                if (await Eval(code)) return;
                await Task.Delay(25);
            }
            throw new TimeoutException("等待失败：" + code);
        }
        try
        {
            Check(WindowState == FormWindowState.Maximized, "默认以最大化窗口打开");
            var workingArea = Screen.FromControl(this).WorkingArea;
            Check(workingArea.Contains(RestoreBounds) && RestoreBounds.Width >= workingArea.Width * .9
                && RestoreBounds.Height >= workingArea.Height * .9, "还原窗口使用当前屏幕约 92% 的工作区");
            string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='800' height='500'><defs><linearGradient id='g'><stop stop-color='#7195ff'/><stop offset='1' stop-color='#77e0cc'/></linearGradient></defs><rect x='20' y='20' width='760' height='460' rx='45' fill='url(#g)'/><text x='400' y='250' text-anchor='middle' font-size='48'>SVG 矢量测试</text><script>parent.__svgExecuted=true</script></svg>";
            foreach (string name in new[] { "图片10.svg", "图片2.SVG", "图片1.svg" })
                File.WriteAllText(Path.Combine(fixture, name), svg);
            File.WriteAllText(Path.Combine(fixture, "ignore.txt"), "ignored");
            var catalog = FileCatalog.Scan(Path.Combine(fixture, "图片1.svg"));
            Check(catalog.Count == 3 && Path.GetFileName(catalog[1]) == "图片2.SVG", "自然排序、中文路径、大小写扩展名与非 SVG 过滤");
            string invalid = Path.Combine(root, "invalid.svg");
            File.WriteAllText(invalid, "<svg xmlns='http://www.w3.org/2000/svg'><g></svg>");
            bool rejected = false;
            try { FileCatalog.ReadSvg(invalid); } catch (System.Xml.XmlException) { rejected = true; }
            Check(rejected, "拒绝损坏的 XML");
            string wrongRoot = Path.Combine(root, "wrong-root.svg");
            File.WriteAllText(wrongRoot, "<html/>");
            rejected = false;
            try { FileCatalog.ReadSvg(wrongRoot); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "拒绝伪 SVG 文件");
            await OpenAsync(catalog[0]);
            await WaitFor("state.loaded && state.index===1");
            Check(await Eval("state.width===800 && state.height===500 && state.fit"), "SVG 渲染、尺寸与自动适应窗口");
            Check(await Eval("window.__svgExecuted===undefined"), "SVG 内嵌脚本不会执行");
            Check(await Eval("$('previous').disabled && !$('next').disabled"), "首张边界按钮状态");
            Check(await Eval("(()=>{const rect=viewport.getBoundingClientRect();const event=new WheelEvent('wheel',{deltaY:-100,clientX:220,clientY:rect.top+180,bubbles:true,cancelable:true});const sx=event.clientX-rect.left,sy=event.clientY-rect.top;let a=(sx-state.x)/state.scale,b=(sy-state.y)/state.scale,s=state.scale;viewport.dispatchEvent(event);return state.scale>s && Math.abs((sx-state.x)/state.scale-a)<.001 && Math.abs((sy-state.y)/state.scale-b)<.001})()"), "滚轮缩放保持鼠标下图片坐标固定");
            // Synthetic pointer events don't activate OS pointer capture; exercise the same handlers with a temporary capture shim.
            Check(await Eval("(()=>{const capture=viewport.setPointerCapture;viewport.setPointerCapture=()=>{};const x=state.x,y=state.y;viewport.dispatchEvent(new PointerEvent('pointerdown',{pointerId:7,button:0,clientX:100,clientY:100}));viewport.dispatchEvent(new PointerEvent('pointermove',{pointerId:7,clientX:170,clientY:140}));viewport.dispatchEvent(new PointerEvent('pointerup',{pointerId:7}));viewport.setPointerCapture=capture;return Math.abs(state.x-x-70)<.001 && Math.abs(state.y-y-40)<.001 && drag===null})()"), "拖动平移及释放状态");
            Check(await Eval("actual();state.scale===1 && !state.fit"), "1:1 原始大小");
            Check(await Eval("setBackground('checker');viewport.dataset.bg==='checker' && document.querySelector('[data-bg=checker].swatch').getAttribute('aria-pressed')==='true'"), "棋盘背景切换");
            await web.CoreWebView2.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true,cancelable:true}));");
            await WaitFor("state.index===2");
            Check(currentPath == catalog[1], "右方向键通过原生桥接切换文件");
            await NavigateAsync(1);
            await WaitFor("state.index===3");
            Check(await Eval("$('next').disabled && !$('previous').disabled && state.fit"), "末张边界与切图后适应窗口");
            await NavigateAsync(1);
            Check(currentPath == catalog[2], "末张不越界");
            await OpenAsync(invalid);
            await WaitFor("$('toast').classList.contains('error')");
            Check(currentPath == catalog[2], "打开损坏文件保留当前图片");
            await OpenAsync(Path.Combine(root, "missing.svg"));
            await WaitFor("$('toast').textContent.includes('missing.svg')");
            Check(currentPath == catalog[2], "缺失文件提示错误并保留当前图片");
            await NavigateAsync(-1);
            await WaitFor("state.index===2");
            Check(currentPath == catalog[1], "上一张切换");
            ToggleFullscreen();
            Check(FormBorderStyle == FormBorderStyle.None, "进入全屏");
            ToggleFullscreen();
            Check(FormBorderStyle == FormBorderStyle.Sizable, "退出全屏恢复窗口");
            WindowState = FormWindowState.Normal;
            ClientSize = new Size(960, 680);
            await Task.Delay(100);
            Check(await Eval("state.fit && Math.abs(state.x-(viewport.clientWidth-state.width*state.scale)/2)<1"), "窗口调整后自动适应");
            Check(await Eval("document.querySelector('.tools').scrollWidth<=document.querySelector('.tools').clientWidth && document.documentElement.scrollWidth<=innerWidth"), "高 DPI 窄窗口工具栏完整显示");
            await RunEpsTestsAsync(root, Check, Eval, WaitFor);
            await web.CoreWebView2.ExecuteScriptAsync("$('toast').style.display='none';setBackground('dark');");
            await Task.Delay(100);
            using (var screenshot = File.Create(Path.Combine(root, "viewer.png")))
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot);
            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, count = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { WriteTestFailure(ex); }
        finally { Close(); }
    }
}

