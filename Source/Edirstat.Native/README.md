# eDirStat scanner source

Imported from https://github.com/xangelix/edirstat at commit
`777a58e678ed1ed1c7eaec8c28725908a03648a9` (upstream 2.2.0), together with
the local C ABI bridge 0.4.0. MIT copyright and permission notice are preserved
in `LICENSE.edirstat` and included in all published packages.

This workspace contains the original core crate, the four scanner modules
(`coordinator`, `mft`, `mounts`, `traversal`) and our bridge. Scanner modules
now live in `crates/edirstat-bridge/src/engine`; the original egui application,
web UI and unrelated packaging tools are not part of BetterTaskManager.

`Source/Edirstat.Client/Edirstat.Client.csproj` builds the Windows x64 release
DLL through Cargo during the regular plugin or standalone build. No other
checkout or prebuilt eDirStat package is needed. See the [disk-space documentation](../../README.md#磁盘空间).
