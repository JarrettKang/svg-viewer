# SVG 查看器

轻量 Windows 桌面 SVG 查看器：滚轮缩放、拖动平移、同文件夹切图。中文界面，使用 WebView2 渲染矢量图片。

## 直接使用

从 [GitHub Releases](https://github.com/JarrettKang/svg-viewer/releases/latest) 下载 `SvgViewer-win-x64.zip`，解压后双击其中的 `SvgViewer.exe`。

从源码构建时，程序生成在 `dist/SvgViewer/SvgViewer.exe`，完整压缩包生成在 `dist/SvgViewer-win-x64.zip`。

发布版包含 .NET 运行时，无需安装 Python 或 .NET。支持 Windows 10/11 x64，需要 Microsoft Edge WebView2 Runtime。若电脑缺少它，可从 [微软官方页面](https://developer.microsoft.com/microsoft-edge/webview2/) 安装 Evergreen Runtime。

打开图片有三种方式：点击“打开图片”、拖入 SVG，或将文件路径作为命令行参数传入。

```powershell
.\SvgViewer.exe "D:\图片文件夹\示意图.svg"
```

附带 `samples` 文件夹，可以用其中三张图片体验切图、透明背景和细节放大。

默认最大化打开，让图片尽量占满可用画布。点击窗口“还原”后，窗口仍占启动时所在屏幕约 92% 的工作区；窗口大小按实际屏幕计算，适配高 DPI 显示器。最大化保留 Windows 标题栏与任务栏，F11 可进入全屏。

## 操作

| 操作 | 鼠标 / 快捷键 |
| --- | --- |
| 打开文件 | Ctrl+O |
| 缩放 | 滚轮，以鼠标位置为中心；或 + / - |
| 平移 | 按住鼠标左键拖动 |
| 上一张 / 下一张 | ← / →；或工具栏箭头 |
| 适应窗口 | 0；或双击画布 |
| 原始大小 | 1 |
| 重新读取当前文件 | R |
| 全屏 | F11 |
| 退出全屏 | Esc / F11 |
| 查看文件位置 | 左下角“在文件夹中显示” |

同文件夹里的 `.svg` 文件按 Windows 自然顺序排列，例如 `2.svg` 在 `10.svg` 前；不递归子文件夹。到首张或末张时停止，不循环。每次切换会重新读取文件夹，以识别新增或删除的图片。背景可选择白色、深色或透明棋盘。

## 设为默认 SVG 程序

1. 先把发布文件夹放到一个固定位置，设置后不要移动或删除程序。
2. 打开程序，点击右上角“帮助” → “设为 SVG 默认应用”。
3. 在打开的 Windows 默认应用设置中搜索 `.svg`，选择“SVG 查看器”。

也可以右键任意 SVG → 打开方式 → 选择其他应用 → 在电脑上选择 `SvgViewer.exe`，然后选择“始终”。不同 Windows 版本的文字略有差异。

注册按钮只添加当前用户的打开候选项，不需要管理员权限。最终默认程序由你在 Windows 设置中选择。设置成功后双击 SVG 会直接启动查看器。程序正常启动不会修改文件关联。

## 显示范围

适合查看本身包含全部内容的 SVG；支持路径、文字、渐变、滤镜和内嵌图片等 WebView2 能渲染的 SVG 图形。图片以浏览器的图片模式加载，SVG 内脚本不会执行。外部图片、外部样式和外部字体不会加载；依赖这些资源的 SVG 需要先将资源嵌入文件。字体使用本机已安装字体，缺失字体可能产生替代显示。

单文件上限 64 MB。打开损坏或缺失文件会显示提示，保留上一张有效图片。程序只读取图片，不改写 SVG。

WebView2 缓存位于 `%LOCALAPPDATA%\SvgViewer\WebView2`。这是本地桌面程序，没有服务器，也不上传图片。

## 开发与验证

源码在 `src`，需要 .NET 8 SDK。首次构建需联网下载官方 NuGet WebView2 包与运行时。

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

测试使用真实 WebView2 窗口和 SVG，验证文件排序、渲染、脚本隔离、缩放坐标、平移事件处理、键盘消息桥接、切图边界、损坏文件、全屏与窗口缩放；拖动测试使用合成指针事件，不代替真实鼠标/系统拖放的人工验收。结果与截图写入 `artifacts/published-test`。测试不修改文件关联。

发布产物在 `dist/SvgViewer`，压缩包在 `dist/SvgViewer-win-x64.zip`。

## 技术参考

使用微软官方 [WebView2 桌面开发指南](https://learn.microsoft.com/microsoft-edge/webview2/get-started/winforms) 和 [WebView2 文件拖放与消息 API](https://learn.microsoft.com/microsoft-edge/webview2/concepts/overview-features-apis)。

## 许可证

项目源码采用 [MIT License](LICENSE)，允许使用、修改与分发。附带的第三方组件遵循其各自许可证，参见 [第三方声明](THIRD-PARTY-NOTICES.md) 及发布包中的 `licenses` 文件夹。
