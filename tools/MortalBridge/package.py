"""Build a portable Windows CPU Mortal package with model, sources and dependency notices.

Requires the pinned dedicated Python environment from prepare.py and MSVC/Rust locally.
The result requires neither Python nor pip on the destination machine.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import urllib.request
import zipfile
from prepare import HERE, LOCK, sha256
from patch_native import patch


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--source", required=True, type=Path)
    p.add_argument("--environment", required=True, type=Path)
    p.add_argument("--model-directory", required=True, type=Path)
    p.add_argument("--destination", required=True, type=Path)
    p.add_argument("--archive", required=True, type=Path)
    p.add_argument("--cargo", default="cargo")
    p.add_argument("--msvc-runtime", required=True, type=Path)
    args = p.parse_args()
    root, source = args.destination.resolve(), args.source.resolve()
    if root.exists() and any(root.iterdir()):
        raise ValueError("Destination must be empty; stale files are not release inputs")
    patch(source)
    env = dict(os.environ, PYO3_PYTHON=str(args.environment.resolve() / "Scripts/python.exe"))
    # Keep build-host paths and PDB references out of the distributable DLL.
    remaps = [f"--remap-path-prefix={source}=/_/mortal"]
    for key in ("CARGO_HOME", "USERPROFILE"):
        if env.get(key): remaps.append(f"--remap-path-prefix={env[key]}=/_/{key.lower()}")
    existing = env.get("CARGO_ENCODED_RUSTFLAGS", "")
    env["CARGO_ENCODED_RUSTFLAGS"] = "\x1f".join(([existing] if existing else []) + remaps)
    env["CARGO_PROFILE_RELEASE_DEBUG"] = "0"
    subprocess.run([args.cargo, "build", "--locked", "--manifest-path", str(source / "Cargo.toml"),
                    "-p", "libriichi", "--lib", "--release"], check=True, env=env)
    root.mkdir(parents=True, exist_ok=True)
    embed = root.parent / "python-3.13.3-embed-amd64.zip"
    if not embed.exists():
        embed.write_bytes(urllib.request.urlopen("https://www.python.org/ftp/python/3.13.3/python-3.13.3-embed-amd64.zip", timeout=60).read())
    if sha256(embed) != "59ff76e16e6597de47474fb22be69e7191a89116910d728ab735079b078e52db":
        raise ValueError("CPython distribution digest mismatch")
    with zipfile.ZipFile(embed) as z:
        z.extractall(root / "python")
    (root / "python/python313._pth").write_text("python313.zip\n.\nLib/site-packages\n..\n", encoding="ascii")
    site = args.environment / "Lib/site-packages"
    def ignore(directory, names):
        return [n for n in names if n == "__pycache__" or n.endswith((".pyc", ".lib", ".pdb")) or n == "include"]
    shutil.copytree(site, root / "python/Lib/site-packages", dirs_exist_ok=True, ignore=ignore)
    # The Python embeddable package supplies vcruntime; torch additionally needs MSVCP.
    for name in ("msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll", "msvcp140_atomic_wait.dll",
                 "msvcp140_codecvt_ids.dll", "vcruntime140.dll", "vcruntime140_1.dll",
                 "vcruntime140_threads.dll", "concrt140.dll"):
        file = args.msvc_runtime / name
        if file.is_file(): shutil.copy2(file, root / "python" / name)
        elif name in ("msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll"):
            raise ValueError("Missing required x64 retail runtime: " + name)
    runtime = root / "runtime"
    runtime.mkdir(exist_ok=True)
    for name in ("model.py", "engine.py"):
        shutil.copy2(source / "mortal" / name, runtime / name)
    target = Path(env.get("CARGO_TARGET_DIR", source / "target"))
    shutil.copy2(target / "release/riichi.dll", runtime / "libriichi.pyd")
    for name in ("snapshot.py", "rules.py", "runtime.py", "prepare.py", "model-lock.json"):
        shutil.copy2(HERE / name, root / name)
    for name in (LOCK["checkpoint"], "README.md", "model-manifest.json", "Mortal-LICENSE"):
        shutil.copy2(args.model_directory / name, root / name)
    if sha256(root / LOCK["checkpoint"]) != LOCK["checkpoint_sha256"]:
        raise ValueError("Model digest mismatch")
    shutil.copy2(source / "LICENSE", root / "Mortal-source-LICENSE")
    # Exact corresponding modified source, with upstream license and lockfile.
    with zipfile.ZipFile(root / "Mortal-Corresponding-Source.zip", "w", zipfile.ZIP_DEFLATED) as z:
        tracked = subprocess.check_output(["git", "-C", str(source), "ls-files"], text=True).splitlines()
        for name in tracked + ["libriichi/src/state/mjcn_public.rs"]:
            path = source / name
            if path.is_file():
                z.write(path, name)
        for path in HERE.glob("*.py"):
            z.write(path, "mjcn/" + path.name)
        z.write(HERE / "model-lock.json", "mjcn/model-lock.json")
        z.write(HERE / "native/mjcn_public.rs", "mjcn/native/mjcn_public.rs")
    (root / "NOTICE-MJCN.txt").write_text(
        "MJCN Mortal public snapshot bridge v2. AGPL-3.0-or-later.\n"
        "Upstream: Equim-chan/Mortal " + LOCK["source_commit"] + "\n"
        "Model publisher: " + LOCK["model_url"] + "/tree/" + LOCK["model_revision"] + "\n"
        "Model is a community four-player V4 checkpoint, not an official Mortal release.\n"
        "Corresponding modified native source is included in Mortal-Corresponding-Source.zip.\n"
        "MJCN source: https://github.com/Kamishiroex/MahjongSolver-CN\n"
        "CPython 3.13.3: https://www.python.org/downloads/release/python-3133/ (python/LICENSE.txt).\n"
        "PyTorch 2.9.1+cpu: https://github.com/pytorch/pytorch/tree/v2.9.1\n"
        "NumPy 2.3.3: https://github.com/numpy/numpy/tree/v2.3.3\n"
        "Python wheel dependencies retain their .dist-info licenses and notices under python/Lib/site-packages.\n"
        "Microsoft C/C++ runtime: https://visualstudio.microsoft.com/license-terms/vs2022-cruntime/\n"
        "Only public table information is accepted. Partial chronology remains approximate model input.\n", encoding="utf-8")
    manifest = {"schema": 1, "source_commit": LOCK["source_commit"], "model_revision": LOCK["model_revision"],
                "files": {str(f.relative_to(root)).replace("\\", "/"): sha256(f) for f in
                          [root / LOCK["checkpoint"], *[runtime / n for n in ("model.py", "engine.py", "libriichi.pyd")]]}}
    (root / "runtime.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    # A bridge identity is not evidence of correct inputs. Gate every new package on
    # full-tensor native replay comparison plus a real checkpoint inference smoke test.
    subprocess.run([str(root / "python/python.exe"), "-B", str(HERE / "feature_replay.py"),
                    "--native-directory", str(runtime), "--report", str(root / "feature-validation.json")], check=True)
    subprocess.run([str(root / "python/python.exe"), "-B", str(HERE / "check_snapshot.py"),
                    "--directory", str(root), "--repository", str(HERE.parent.parent),
                    "--report", str(root / "snapshot-validation.json")], check=True)
    files = sorted(f for f in root.rglob("*") if f.is_file() and f.name != "mortal-installation.json")
    installation = {"bridge": "mjcn-mortal-public-v2", "source_commit": LOCK["source_commit"], "model_sha256": LOCK["checkpoint_sha256"],
                    "files": {f.relative_to(root).as_posix(): sha256(f) for f in files}}
    (root / "mortal-installation.json").write_text(json.dumps(installation, indent=2), encoding="utf-8")
    args.archive.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(args.archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for f in files + [root / "mortal-installation.json"]:
            z.write(f, f.relative_to(root).as_posix())
    print(json.dumps({"archive": str(args.archive), "sha256": sha256(args.archive), "files": len(files) + 1, "bytes": args.archive.stat().st_size}))

if __name__ == "__main__":
    main()
