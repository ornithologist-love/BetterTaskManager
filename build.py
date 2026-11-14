#!/usr/bin/env python3
"""Unified build script for BetterTaskManager (BTM)."""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import uuid
import zipfile
from pathlib import Path

HOST_RUNTIME_DLL_RE = re.compile(
    r"^(Avalonia(?:\.|$)|Semi\.|FluentIcons\.|Heron\.Client(?:Framework|Controls)|"
    r"CommunityToolkit\.Mvvm(?:\.|$)|Microsoft\.Extensions\.|Serilog(?:\.|$))"
)

ALL_TARGETS = ("disk", "systeminformer", "plugin", "app", "nuget")


def read_manifest(repo: Path) -> dict:
    manifest_path = repo / "Facade" / "Heron" / "Client.Plugin.SystemInformer" / "plugin.json"
    return json.loads(manifest_path.read_text(encoding="utf-8"))


def make_staging(binary_root: Path) -> Path:
    staging = binary_root / "Staging" / uuid.uuid4().hex
    staging.parent.mkdir(parents=True, exist_ok=True)
    return staging


def assert_inside_binaries(binary_root: Path, candidate: Path) -> Path:
    resolved = candidate.resolve()
    root_resolved = binary_root.resolve()
    if root_resolved not in resolved.parents:
        raise RuntimeError(f"Artifact path must be inside {root_resolved}: {resolved}")
    return resolved


def finalize_output(binary_root: Path, staging: Path, output: Path) -> Path:
    assert_inside_binaries(binary_root, staging)
    assert_inside_binaries(binary_root, output)
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        shutil.rmtree(output)
    shutil.move(str(staging), str(output))
    return output


def run_dotnet(repo: Path, args: list[str]) -> None:
    env = os.environ.copy()
    prev_rustflags = env.get("RUSTFLAGS", "").strip()
    home = str(Path.home())
    remap = f"--remap-path-prefix={repo}=/_/src/ --remap-path-prefix={home}=/_/home/"
    env["RUSTFLAGS"] = f"{prev_rustflags} {remap}".strip()

    cmd = ["dotnet", *args]
    print(f"+ {' '.join(cmd)}", flush=True)
    result = subprocess.run(cmd, cwd=repo, env=env)
    if result.returncode != 0:
        raise SystemExit(result.returncode)


def verify_required(staging: Path, files: list[str], label: str) -> None:
    for name in files:
        if not (staging / name).is_file():
            raise RuntimeError(f"Missing {label} file: {name}")


def build_disk(repo: Path, binary_root: Path, version: str, cfg: str, framework_feed: str | None, self_contained: bool) -> None:
    project = repo / "Facade" / "Standalone" / "Edirstat.App" / "Edirstat.App.csproj"
    staging = make_staging(binary_root)
    output = binary_root / "Edirstat.App" / version
    args = [
        "publish", str(project),
        "-c", cfg,
        "-r", "win-x64",
        "--self-contained", "true" if self_contained else "false",
        "-o", str(staging),
        "--nologo",
    ]
    if framework_feed:
        args.append(f"-p:HeronFrameworkFeed={Path(framework_feed).resolve()}")
    run_dotnet(repo, args)
    verify_required(staging, ["Edirstat.App.exe", "Edirstat.Client.dll", "edirstat_bridge.dll", "LICENSE.edirstat", "EDIRSTAT.md"], "disk app")
    finalize_output(binary_root, staging, output)
    print(f"Disk app: {output / 'Edirstat.App.exe'}")


def build_systeminformer(repo: Path, binary_root: Path, version: str, cfg: str, framework_feed: str | None) -> None:
    project = repo / "Facade" / "SystemInformer.PluginFacade" / "SystemInformer.PluginFacade.csproj"
    staging = make_staging(binary_root)
    output = binary_root / "SystemInformer" / version
    args = [
        "publish", str(project),
        "-c", cfg,
        "-o", str(staging),
        "--nologo",
    ]
    if framework_feed:
        args.append(f"-p:HeronFrameworkFeed={Path(framework_feed).resolve()}")
    run_dotnet(repo, args)
    verify_required(staging, ["SystemInformer.Client.dll", "SystemInformer.PluginFacade.dll", "Edirstat.Client.dll", "edirstat_bridge.dll"], "systeminformer")
    finalize_output(binary_root, staging, output)
    print(f"SystemInformer DLLs: {output}")


def build_plugin(repo: Path, binary_root: Path, manifest: dict, cfg: str, framework_feed: str | None) -> None:
    project = repo / "Facade" / "Heron" / "Client.Plugin.SystemInformer" / "Heron.Plugin.BTM.csproj"
    staging = make_staging(binary_root)
    output = binary_root / manifest["id"] / manifest["version"]
    args = [
        "publish", str(project),
        "-c", cfg,
        "-o", str(staging),
        "--nologo",
        "-p:DownloadFFmpegOnBuild=false",
    ]
    if framework_feed:
        args.append(f"-p:HeronFrameworkFeed={Path(framework_feed).resolve()}")
    run_dotnet(repo, args)

    for dll in staging.rglob("*.dll"):
        if HOST_RUNTIME_DLL_RE.match(dll.name):
            dll.unlink()

    verify_required(staging, ["plugin.json", "Heron.Plugin.BTM.dll"], "plugin")
    for name in ("LICENSE", "THIRD-PARTY-NOTICES.md"):
        shutil.copy2(repo / name, staging / name)
    finalize_output(binary_root, staging, output)
    print(f"Plugin package: {output}")


def build_nuget(repo: Path, binary_root: Path, manifest: dict, cfg: str, framework_feed: str | None) -> None:
    if cfg != "Release":
        raise RuntimeError("NuGet delivery requires Release.")
    build_plugin(repo, binary_root, manifest, cfg, framework_feed)
    project = repo / "Source" / "BTM.Package" / "Heron.Plugin.BTM.Package.csproj"
    output = binary_root / "NuGet"
    args = ["pack", str(project), "-c", "Release", "-o", str(output), "--nologo"]
    if framework_feed:
        args.append(f"-p:HeronFrameworkFeed={Path(framework_feed).resolve()}")
    run_dotnet(repo, args)
    archive = output / f"Heron.Plugin.BTM.{manifest['version']}.nupkg"
    with zipfile.ZipFile(archive) as package:
        names = set(package.namelist())
        required = {"plugin/plugin.json", "plugin/Heron.Plugin.BTM.dll", "plugin/edirstat_bridge.dll",
                    "plugin/SystemInformer.Client.dll", "plugin/SystemInformer.PluginFacade.dll", "plugin/Edirstat.Client.dll",
                    "plugin/LICENSE", "plugin/LICENSE.edirstat", "plugin/THIRD-PARTY-NOTICES.md",
                    "build/Heron.Plugin.BTM.targets", "LICENSE", "README.md"}
        if not required.issubset(names):
            raise RuntimeError("Incomplete NuGet plugin payload: " + ", ".join(sorted(required - names)))
        if any(HOST_RUNTIME_DLL_RE.match(Path(name).name) for name in names if name.endswith(".dll")):
            raise RuntimeError("NuGet plugin must use the host's shared framework assemblies.")
        packed = json.loads(package.read("plugin/plugin.json"))
        if packed != manifest:
            raise RuntimeError("NuGet plugin identity differs from its source manifest.")
    print(f"NuGet package: {archive}")


def build_app(repo: Path, binary_root: Path, version: str, cfg: str, framework_feed: str | None, self_contained: bool) -> None:
    project = repo / "Facade" / "Standalone" / "SystemInformer.App" / "SystemInformer.App.csproj"
    staging = make_staging(binary_root)
    output = binary_root / "BetterTaskManager" / version
    args = [
        "publish", str(project),
        "-c", cfg,
        "-r", "win-x64",
        "--self-contained", "true" if self_contained else "false",
        "-o", str(staging),
        "--nologo",
    ]
    if framework_feed:
        args.append(f"-p:HeronFrameworkFeed={Path(framework_feed).resolve()}")
    run_dotnet(repo, args)
    verify_required(
        staging,
        [
            "BetterTaskManager.exe",
            "SystemInformer.Client.dll",
            "SystemInformer.PluginFacade.dll",
            "Edirstat.Client.dll",
            "edirstat_bridge.dll",
            "Heron.ClientControls.dll",
        ],
        "standalone app",
    )
    finalize_output(binary_root, staging, output)
    print(f"Standalone app: {output / 'BetterTaskManager.exe'}")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build BetterTaskManager (BTM) targets.")
    parser.add_argument(
        "-t", "--target",
        nargs="+",
        choices=[*ALL_TARGETS, "all"],
        default=["plugin"],
        help="Build target(s): disk, systeminformer, plugin, app, nuget, or all (default: plugin).",
    )
    parser.add_argument(
        "-c", "--configuration",
        choices=["Debug", "Release"],
        default="Release",
        help="Build configuration (default: Release).",
    )
    parser.add_argument(
        "--framework-feed",
        help="Optional path to local Heron framework NuGet packages.",
    )
    parser.add_argument(
        "--framework-dependent",
        action="store_true",
        help="Publish standalone app/disk without bundling the .NET runtime.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    repo = Path(__file__).resolve().parent
    binary_root = repo / "Binaries"
    manifest = read_manifest(repo)
    version = manifest["version"]
    self_contained = not args.framework_dependent

    targets: list[str] = []
    for t in args.target:
        if t == "all":
            for item in ALL_TARGETS:
                if item not in targets:
                    targets.append(item)
        elif t not in targets:
            targets.append(t)

    for t in targets:
        if t == "disk":
            build_disk(repo, binary_root, version, args.configuration, args.framework_feed, self_contained)
        elif t == "systeminformer":
            build_systeminformer(repo, binary_root, version, args.configuration, args.framework_feed)
        elif t == "plugin":
            build_plugin(repo, binary_root, manifest, args.configuration, args.framework_feed)
        elif t == "app":
            build_app(repo, binary_root, version, args.configuration, args.framework_feed, self_contained)
        elif t == "nuget":
            build_nuget(repo, binary_root, manifest, args.configuration, args.framework_feed)

    return 0


if __name__ == "__main__":
    sys.exit(main())
