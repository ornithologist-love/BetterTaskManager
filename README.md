# BTM — Better Task Manager

Windows x64 的 Avalonia 任务管理器，也可作为 Heron.ClientFramework 插件加载。

- 进程：进程树、CPU、私有工作集、磁盘与网络速率。
- 性能：CPU、内存、磁盘、网络与图形设备。
- 服务：查询、筛选和 Windows 原生服务管理。
- 磁盘：基于 Rust eDirStat 扫描目录占用，用户点击后才开始扫描。

插件 ID 为 `heron.btm`，版本 `0.3.0`，宿主接口版本 `4`。
首次打开页面才启动采样，切走立即停止；界面跟随 Heron 明暗主题。

## 构建

需要 .NET 8 SDK、Python 3、Rust stable MSVC x64 工具链，以及 Heron 框架包 0.2.8。
默认使用工作区相邻 `Binaries/Heron` 本地包源，也可通过 `--framework-feed` 显式提供。

```powershell
python build.py --target plugin -c Release          # Heron 插件包 -> Binaries/heron.btm/0.3.0
python build.py --target app -c Release             # 独立 Windows 应用 -> Binaries/BetterTaskManager/0.3.0/BetterTaskManager.exe
python build.py --target disk -c Release            # 独立磁盘扫描应用 -> Binaries/Edirstat.App/0.3.0/Edirstat.App.exe
python build.py --target systeminformer -c Release  # 仅编译 SystemInformer DLL -> Binaries/SystemInformer/0.3.0
python build.py --target all -c Release             # 全部编译
python build.py --target plugin --framework-feed <framework-feed>
```

插件产物位于 `Binaries/heron.btm/0.3.0`，独立 Windows 应用产物位于 `Binaries/BetterTaskManager/0.3.0/BetterTaskManager.exe`。宿主 Edition 显式声明插件 ID、包路径后加载；
本仓不依赖消费方源码或私有 SDK。编译继续使用各项目正常的 `bin`、`obj` 目录。

## 磁盘空间

磁盘页面基于上游 eDirStat 2.2.0 的 Rust 原生扫描后端（`Source/Edirstat.Native`）与 C#/Avalonia 界面（`Source/Edirstat.Client`）：

- **视图与交互**：包含目录树（按需分批加载）、文件类型占比、Top 1,000 大文件列表与 squarified 矩形分布图；支持分隔条调整、点击定位与双击放大。
- **右键与回收站**：支持打开所在目录、复制路径/大小、切换分配大小、显示卷可用空间，以及经确认后移入 Windows 回收站。
- **权限与内存**：普通目录扫描无需管理员权限（在具备权限的 NTFS 卷上尝试 MFT 快速扫描，否则回退目录遍历）；页面隐藏 5 分钟后自动释放 Rust 侧扫描结果，避免长期常驻占用。
- **独立运行**：可通过 `python build.py --target disk -c Release` 打包独立窗口 `Edirstat.App.exe`（或双击 `Launch-Disk.cmd`），支持传入 `--scan <path>` 启动后自动扫描。

## 源码与许可证

`Source` 维护托管页面和原生扫描器；`Facade` 只负责 Heron 插件或独立窗口的接线。
主代码使用 MIT；eDirStat 的 MIT 许可证和来源保存在
`Source/Edirstat.Native/LICENSE.edirstat` 与 `THIRD-PARTY-NOTICES.md`。
