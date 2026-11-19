# Legado·UI3

Windows x64 阅读器，兼容 [Legado（阅读 3.0）](https://github.com/gedoor/legado) 的书源规则。书架、搜索、书源、扩展和设置使用 WinUI 3，阅读排版使用 WebView2。

## 使用

从 [GitHub Releases](https://github.com/ShenShenSshen/Legado.UI3/releases/latest) 下载 `Legado.UI3-v版本-win-x64.zip`。完整解压到一个文件夹，再运行 `Legado.UI3.exe`。应用使用系统的 Microsoft Edge WebView2 Runtime。

「设置 → 关于」显示当前版本，可检查正式发布并下载 ZIP。下载依次尝试 `gh-proxy.org`、`ghfast.top` 和 GitHub 原站，并以 GitHub 发布记录中的 SHA-256 校验文件。更新时退出当前应用，把新版解压到新文件夹后运行，书架等数据仍从本机数据目录读取。

- 在「书架」导入 TXT / EPUB，或拖放文件。阅读器支持滚动续章、翻页、目录、书签、换源和朗读。
- 阅读背景默认跟随应用主题。深色应用会使用夜间底色。需要独立设置时，可在「阅读设置 → 排版 → 背景」选择纸白、米黄、浅绿或夜间。
- 在「设置 → 朗读 → 导入网络语音」选择 Legado 的 `httpTTS.json`，或粘贴 HTTPS 接口，例如 `https://your-server.example/tts?text={{speakText}}`。导入后从音色列表选择并试听。配置只保存在本机，选中网络音色并朗读后，对应服务会收到正文。
- 在「搜索」切换到「网页」可搜索书名或输入网址。浏览页面时，用「识别书籍」提取目录或书籍链接。
- 「设置」的各项内容按小标题在一页展开。「搜索与网络」中的两种检索范围均可选择「全部」，覆盖已启用且配置了搜索规则的书源。
- 在侧栏「浏览」筛选并切换书源，选择该源提供的分类或点击榜、人气榜等入口。点击书籍可预览目录、试读或加入书架；支持分页的书单可翻页。「书源」的编辑页可切换是否在浏览页显示。
- 在「书源 → 导入 → 网络链接」粘贴 GitHub 文件页、Raw 或 HTTPS JSON 链接。预览默认勾选新增项。勾选重复项并开启更新时，保留该书源的启用状态和 ID。
- 在「书源 → 批量选择」勾选书源或全选筛选结果。删除时会显示所选数量和受影响的书籍数量。已缓存章节仍可读取。
- 迁移旧版时，在原应用导出包含本地书的完整备份，再到新版「设置 → 数据与备份 → 从备份恢复」。支持旧版和 Legado 备份，书签和进度随备份迁移。旧阅读历史和统计字段会被忽略。

## 构建与测试

网络朗读支持返回音频的 GET / POST 接口，可带请求头、本地 HTTP 服务，以及常用 Legado 文本和语速占位符。需要登录脚本、自定义 JavaScript 或返回 JSON 包装音频的配置会提示不兼容。

使用 .NET 10 SDK 在 Windows x64 上运行：

```powershell
dotnet publish .\Legado.UI3.csproj -c Release -p:Platform=x64 -o .\publish\win-x64
.\Tests\Run-Smoke.ps1
.\Tests\Run-Smoke.ps1 -GitHub -WebDav
```

测试使用临时书库，不接入真实云盘。`-GitHub` 下载上游公开测试文件。`-WebDav` 需要 Node.js 和 `upstream` 目录中的模拟服务器。

