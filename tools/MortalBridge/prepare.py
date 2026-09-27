"""Prepare a pinned, local-only Mortal experiment; never edits plugin configuration."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import urllib.request

HERE = Path(__file__).resolve().parent
LOCK = json.loads((HERE / "model-lock.json").read_text(encoding="utf-8"))


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def source_identity(source):
    head = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    dirty = subprocess.check_output(["git", "-C", str(source), "status", "--porcelain", "--untracked-files=no"], text=True)
    if head != LOCK["source_commit"] or dirty:
        raise ValueError("Mortal source must be the clean, pinned official commit; preserve experimental forks separately")
    return head


def download(url, target, *, digest=None, size=None):
    target = Path(target)
    if target.is_file() and digest and sha256(target) == digest and target.stat().st_size == size:
        return
    partial = target.with_suffix(target.suffix + ".download")
    # urllib uses the Windows user's configured proxy, unlike some curl installations.
    with urllib.request.urlopen(url, timeout=60) as response, partial.open("wb") as out:
        count = 0
        while chunk := response.read(1024 * 1024):
            count += len(chunk)
            if count > (size or 1024 * 1024):
                raise ValueError("Download exceeds declared size")
            out.write(chunk)
    if size is not None and (count != size or sha256(partial) != digest):
        raise ValueError("Checkpoint size/SHA-256 mismatch; not installed")
    partial.replace(target)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--source", type=Path, help="Existing clean pinned checkout; otherwise clone into directory/source")
    parser.add_argument("--python", type=Path, help="Existing dedicated environment with pinned torch/numpy; otherwise create one")
    parser.add_argument("--cargo", default="cargo")
    parser.add_argument("--download-only", action="store_true")
    args = parser.parse_args()
    root = args.directory.resolve()
    root.mkdir(parents=True, exist_ok=True)
    base = f'{LOCK["model_url"]}/resolve/{LOCK["model_revision"]}/'
    for name in ("README.md", "model-manifest.json", "Mortal-LICENSE"):
        download(base + name, root / name)
    download(base + LOCK["checkpoint"], root / LOCK["checkpoint"],
             digest=LOCK["checkpoint_sha256"], size=LOCK["checkpoint_bytes"])
    if args.download_only:
        print(json.dumps({"download": "verified", "sha256": sha256(root / LOCK["checkpoint"])}))
        return
    source = args.source.resolve() if args.source else root / "source"
    if not source.exists():
        subprocess.run(["git", "clone", LOCK["source_url"], str(source)], check=True)
        subprocess.run(["git", "-C", str(source), "checkout", "--detach", LOCK["source_commit"]], check=True)
    source_identity(source)
    if args.python:
        python = args.python.resolve()
    else:
        import venv
        venv.EnvBuilder(with_pip=True).create(root / "venv")
        python = root / "venv" / "Scripts" / "python.exe"
        subprocess.run([str(python), "-m", "pip", "install", "torch==" + LOCK["torch_version"],
                        "--index-url", "https://download.pytorch.org/whl/cpu"], check=True)
        subprocess.run([str(python), "-m", "pip", "install", "numpy==" + LOCK["numpy_version"]], check=True)
    check = subprocess.check_output([str(python), "-c", "import torch,numpy,json;print(json.dumps([torch.__version__,numpy.__version__]))"], text=True)
    if json.loads(check) != [LOCK["torch_version"], LOCK["numpy_version"]]:
        raise ValueError("Dedicated environment versions differ from model-lock.json")
    env = dict(os.environ, PYO3_PYTHON=str(python))
    subprocess.run([args.cargo, "build", "--locked", "--manifest-path", str(source / "Cargo.toml"),
                    "-p", "libriichi", "--lib", "--release"], check=True, env=env)
    target = Path(env.get("CARGO_TARGET_DIR", source / "target")).resolve()
    runtime = root / "runtime"
    runtime.mkdir(exist_ok=True)
    for name in ("model.py", "engine.py"):
        shutil.copy2(source / "mortal" / name, runtime / name)
    shutil.copy2(target / "release" / "riichi.dll", runtime / "libriichi.pyd")
    shutil.copy2(source / "LICENSE", root / "Mortal-source-LICENSE")
    manifest = {"schema": 1, "source_commit": LOCK["source_commit"], "python": str(python),
                "model_revision": LOCK["model_revision"],
                "files": {str(p.relative_to(root)).replace("\\", "/"): sha256(p)
                          for p in [root / LOCK["checkpoint"], *[runtime / n for n in ("model.py", "engine.py", "libriichi.pyd")]]}}
    (root / "runtime.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps({"prepared": str(root), "live_plugin_enabled": False}))


if __name__ == "__main__":
    main()
