# BTM — Better Task Manager

[![NuGet](https://img.shields.io/nuget/v/Heron.Plugin.BTM.svg?label=Heron.Plugin.BTM)](https://www.nuget.org/packages/Heron.Plugin.BTM/0.3.0)

基于 Avalonia 的 Windows x64 任务管理器，既可作为独立桌面应用运行，也可作为 `Heron.ClientFramework` 插件接入宿主（插件 ID：`heron.btm`，版本：`0.3.0`，`host_api: 4`）。页面首次打开时才启动采样，切走立即停止，界面跟随明暗主题。

## 功能

- **进程**：进程树、CPU、私有工作集、磁盘与网络速率。
- **性能**：CPU、内存、磁盘、网络与图形设备实时监控。
- **启动项**：查看与管理 Windows 开机启动项。
- **服务**：查询、筛选与 Windows 原生服务管理。
- **磁盘**：基于 Rust eDirStat 2.2.0 扫描目录占用（点击后才开始扫描）：
  - 目录树按需分批加载、文件类型占比、Top 1,000 大文件列表与 squarified 矩形分布图（支持分隔条调整、点击定位与双击放大）。
  - 支持打开所在目录、复制路径/大小、切换分配大小、显示卷可用空间，以及经确认后移入 Windows 回收站。
  - 普通目录扫描无需管理员权限（在有权限的 NTFS 卷上尝试 MFT 快速扫描，否则回退目录遍历）；页面隐藏 5 分钟后自动释放 Rust 侧扫描结果，避免长期常驻占用。

## 作为 Heron 插件安装（NuGet）

[`Heron.Plugin.BTM`](https://www.nuget.org/packages/Heron.Plugin.BTM/0.3.0) 提供了包含完整预编译产物的 NuGet 包（固定目标 Windows x64、.NET 8、宿主接口版本 `4`），宿主直接还原包即可，无需本仓源码、Python 或 Rust 工具链：

```powershell
dotnet add package Heron.Plugin.BTM --version 0.3.0
```

```xml
<PackageReference Include="Heron.Plugin.BTM" Version="0.3.0" PrivateAssets="all" />
```

- 构建时包内置的 MSBuild target 会自动将插件复制到 `$(TargetDir)plugins/heron.btm/0.3.0/`（可通过 `HeronBtmPluginOutput` 属性自定义输出目录）。
- 支持单独执行 `StageHeronBtmPlugin` target 准备插件目录，并在宿主 Edition 中显式启用 `heron.btm`。

## 从源码构建

需要 `.NET 8 SDK`、`Python 3` 与 `Rust stable MSVC x64` 工具链；构建 Heron 插件包另需 `Heron` 框架包 `0.2.12`（默认查找相邻 `Binaries/Heron`，也可通过 `--framework-feed <path>` 指定）。

```powershell
python build.py --target app -c Release             # 独立桌面应用 -> Binaries/BetterTaskManager/0.3.0/BetterTaskManager.exe
python build.py --target disk -c Release            # 独立磁盘扫描应用 -> Binaries/Edirstat.App/0.3.0/Edirstat.App.exe
python build.py --target plugin -c Release          # Heron 插件包 -> Binaries/heron.btm/0.3.0
python build.py --target nuget -c Release           # 打包 NuGet -> Binaries/NuGet/Heron.Plugin.BTM.0.3.0.nupkg
python build.py --target systeminformer -c Release  # 仅编译 SystemInformer DLL -> Binaries/SystemInformer/0.3.0
python build.py --target all -c Release             # 全部构建
```

独立磁盘扫描器 `Edirstat.App.exe`（或根目录 `Launch-Disk.cmd`）支持传入 `--scan <path>` 启动后自动扫描指定目录。

## 源码结构与许可证

- `Source/`：托管界面（`SystemInformer.Client`、`Edirstat.Client`）与原生磁盘扫描器（`Edirstat.Native`）。
- `Facade/`：Heron 插件接入与独立窗口启动外壳。

主代码采用 MIT 许可证；eDirStat 的 MIT 许可证与第三方声明见 `Source/Edirstat.Native/LICENSE.edirstat` 与 `THIRD-PARTY-NOTICES.md`。
