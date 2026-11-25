<p align="center">
  <img src="Assets/Legado.UI3.png" width="88" alt="Legado.UI3 图标">
</p>

<h1 align="center">Legado·UI3</h1>

<p align="center">Windows x64 阅读器，基于 Legado 书源生态，使用 WinUI 3 构建</p>

<p align="center"><a href="https://github.com/ShenShenSshen/Legado.UI3/releases/latest">下载 Windows x64 安装版或便携版</a></p>

## 项目介绍

Legado·UI3 以 [Legado 手机端（阅读 3.0）](https://github.com/gedoor/legado) 的书源生态和阅读流程为基础，面向 Windows 重新设计了书架、搜索、书源管理和阅读器。已有 Legado 书源可通过 JSON 文件或网络链接导入，网络朗读也支持常用的 Legado TTS 配置。

桌面版使用 C# 和 WinUI 3 重新实现，支持大屏排版、键盘操作、本地书文件拖放和 Windows 系统朗读。Legado·UI3 由本仓库独立维护。

## 功能

- **书架** — 导入或拖放 TXT、EPUB，按分组管理书籍，在封面网格和紧凑列表间切换。选书即可查看详情和进度。从阅读器返回时，恢复书架的滚动位置和选中的书。
- **阅读器** — 支持滚动和翻页，可调节字体、行距、正文宽度及夜间底色。续读能定位到段落内，调整字号或窗口大小后仍可接着原来的文字读。目录、书签、排版和朗读设置从右侧浮出，正文位置保持稳定。网页章节支持提前加载下一章和会话内正文复用。
- **搜索与发现** — 按书名或作者检索启用的书源，可同时搜索全部书源。切换书源浏览分类、榜单和书单，预览目录或试读后加入书架。
- **书源管理** — 导入 Legado 书源 JSON、本地文件或 GitHub 网络链接。支持分组、启停、检测、批量选择和删除。
- **朗读** — 选择 Windows 系统音色，或导入 Legado TTS 配置和网络接口。支持试听、调节语速、读完续章和批量删除网络音色。
- **备份与更新** — 支持本地备份恢复及 WebDAV 同步。启动时检查正式版，有新版才提示。确认后通过国内加速线路或 GitHub 下载，校验后保存进度并重启。

本地备份包含已导入的网络音色与当前音色选择。恢复时会合并音色，保留本机已有的其他音色。WebDAV 默认不上传音色，在「设置 → 云端同步」开启「云备份包含网络音色」后，手动和自动备份才会包含音色、选择及接口凭据。

## 界面截图

书架和阅读器截图为 v1.0.3 的演示界面。书源管理截图展示实际导入的 333 条书源，网址仅显示站点域名。

### 书架

![书架界面，展示封面网格、分组和继续阅读入口](docs/screenshots/bookshelf.png)

### 阅读器

![阅读界面，展示正文排版、目录、书签和朗读入口](docs/screenshots/reader.png)

### 书源管理

![333 条书源的管理界面，展示导入、筛选、检测和批量选择](docs/screenshots/sources.png)

## 技术栈

| 技术 | 在项目中的用途 |
| --- | --- |
| **C#、.NET 10、WinUI 3、Windows App SDK** | 构建 Windows 原生窗口、导航、书架、书源管理和设置页面。 |
| **Microsoft Edge WebView2、HTML、CSS、JavaScript** | 渲染阅读器正文和分页，运行书源规则适配逻辑，处理需要网页渲染的章节。 |
| **Windows Speech Synthesis 与 HTTP TTS** | 播放系统朗读音色，或连接支持的网络语音服务。 |
| **JSON、ZIP 与 WebDAV** | 保存本机书架、书源和进度，提供备份导出、恢复及云端同步。 |
| **GitHub Releases、SHA-256 与 Inno Setup** | 分发并校验更新包，构建 Windows x64 EXE 安装版。 |

## 下载与运行

从 [GitHub Releases](https://github.com/ShenShenSshen/Legado.UI3/releases/latest) 选择 Windows x64 版本：

- **安装版** `*-win-x64-Setup.exe`。运行安装程序，完成后从开始菜单打开 Legado.UI3。可以选择创建桌面快捷方式。
- **便携版** `*-win-x64.zip`。完整解压到一个文件夹，运行 `Legado.UI3.exe`。

应用使用系统的 Microsoft Edge WebView2 Runtime。卸载安装版时会保留本机的书架、书源、阅读进度和音色。

「设置 → 关于」可以手动检查更新。自动更新从 v1.0.1 起可用，安装版和便携版均支持。书架、书源、进度和音色保存在本机数据目录。

## 构建与测试

在 Windows x64 上使用 .NET 10 SDK：

```powershell
dotnet publish .\Legado.UI3.csproj -c Release -p:Platform=x64 -o .\publish\win-x64
.\Tests\Run-Smoke.ps1
```

测试使用独立的临时数据目录。第三方资源许可随资源一并提供，见 [Assets](Assets/)。

生成安装版时，先执行上面的发布命令，再使用 Inno Setup 的 `ISCC.exe /DAppVersion=1.0.4 .\tools\Installer.iss` 编译安装器。
