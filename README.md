<p align="center">
  <img src="Assets/Legado.UI3.png" width="88" alt="Legado.UI3 图标">
</p>

<h1 align="center">Legado·UI3</h1>

<p align="center">兼容 Legado 书源规则的 Windows x64 阅读器</p>

<p align="center"><a href="https://github.com/ShenShenSshen/Legado.UI3/releases/latest">下载 Windows x64 便携版</a></p>

## 功能

- **书架与阅读** — 导入或拖放 TXT、EPUB，按分组管理书籍。支持翻页、连续滚动、目录、书签和进度恢复，可调节字体、行距、正文宽度和夜间底色。
- **搜索与发现** — 按书名或作者检索启用的书源，可同时搜索全部书源。切换书源浏览分类、榜单和书单，预览目录或试读后加入书架。
- **书源管理** — 导入 Legado 书源 JSON、本地文件或 GitHub 网络链接。支持分组、启停、检测、批量选择和删除。
- **朗读** — 选择 Windows 系统音色，或导入 Legado TTS 配置和网络接口。支持试听、调节语速、读完续章和批量删除网络音色。
- **备份与更新** — 支持本地备份恢复及 WebDAV 同步。启动时检查正式版，有新版才提示。确认后通过国内加速线路或 GitHub 下载，校验后保存进度并重启。

## 界面截图

截图中的书籍、正文和书源均为演示数据。

### 书架

![书架界面，展示封面网格、分组和继续阅读入口](docs/screenshots/bookshelf.png)

### 阅读器

![阅读界面，展示正文排版、目录、书签和朗读入口](docs/screenshots/reader.png)

### 书源管理

![书源管理界面，展示导入、筛选、检测和批量选择](docs/screenshots/sources.png)

## 下载与运行

1. 从 [GitHub Releases](https://github.com/ShenShenSshen/Legado.UI3/releases/latest) 下载 Windows x64 ZIP。
2. 完整解压到一个文件夹，运行 `Legado.UI3.exe`。应用使用系统的 Microsoft Edge WebView2 Runtime。

「设置 → 关于」可以手动检查更新。首次使用自动更新前，请先解压运行 v1.0.1 或更新的版本。书架、书源、进度和音色保存在本机数据目录。

## 构建与测试

在 Windows x64 上使用 .NET 10 SDK：

```powershell
dotnet publish .\Legado.UI3.csproj -c Release -p:Platform=x64 -o .\publish\win-x64
.\Tests\Run-Smoke.ps1
```

测试使用独立的临时数据目录。第三方资源许可随资源一并提供，见 [Assets](Assets/)。
