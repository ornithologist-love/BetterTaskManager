# BTM development

- 保持 `Source`、`Facade`、`Docs` 和 `Binaries` 的目录约定；过程材料放 `.work`。
- 范围限于本机进程、性能、服务和磁盘空间。Source 不依赖消费方源码、程序集或私有服务。
- 依赖方向是 `Facade/Heron -> SystemInformer.PluginFacade -> Source`。
- 统一构建入口为 `python build.py`（支持 `--target plugin|app|disk|systeminformer|all`）；独立窗口也可直接 `dotnet run --project Facade/Standalone/SystemInformer.App`。
- 包由公开 Heron.ClientFramework 契约编译，交付到 `Binaries/<id>/<version>`；不做隔离编译。
- CsWin32 只用于构建；NativeMethods.txt 列出具体 API，不使用通配符，不引入驱动。
- 保留 eDirStat 原始许可证和来源；不复制 System Informer/phlib 等其他实现。
- 不新增业务测试；不得提交缓存、运行数据、凭据、SDK 副本或编译产物。
- Git 推送、远程部署和发布需要用户明确授权。

- 仓库只保留 master 分支，初始交付为脱敏后的单个根提交。
