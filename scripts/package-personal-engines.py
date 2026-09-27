"""Make one LOCAL personal-transfer bundle. Modified akochan is never a public release asset."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mortal", required=True, type=Path)
    parser.add_argument("--akochan", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    packages = []
    specs = [(args.mortal, "personal-mortal-v4-582500", "凡夫 Mortal V4 582500", "Mortal", "mortal-installation.json"),
             (args.akochan, "personal-akochan-v5", "akochan v5", "Akochan", "akochan-installation.json")]
    for path, model_id, name, backend, manifest in specs:
        with zipfile.ZipFile(path) as archive:
            if manifest not in archive.namelist():
                raise ValueError("Required installation manifest missing: " + backend)
        with path.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest()
        packages.append(dict(Id=model_id, Name=name, Backend=backend, Archive=backend.lower() + ".zip", Sha256=digest))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(args.output, "x", compression=zipfile.ZIP_STORED) as bundle:
        bundle.writestr("bundled-engines.json", json.dumps(packages, ensure_ascii=False, indent=2))
        for spec, package in zip(specs, packages):
            bundle.write(spec[0], package["Archive"])
        bundle.writestr("个人使用说明.txt", "个人双模型迁移包，包含现有 v5 与凡夫运行环境。\n"
            "更新插件后，/mjcn → AI 设置 → AI 包 ZIP 路径，选择本包并点击导入并使用。\n"
            "无需解压、无需安装 Python。导入完成后在主界面选择模型，再点击手动提醒或自动打牌。\n"
            "两个模型分别保存，切换无需重新导入。以后可继续导入兼容运行包。\n"
            "保留各运行包中的来源与许可证。本个人包含修改版 akochan，不应上传公开发布。\n")
    with args.output.open("rb") as source:
        digest = hashlib.file_digest(source, "sha256").hexdigest()
    print(json.dumps(dict(sha256=digest, bytes=args.output.stat().st_size, engines=[p["Backend"] for p in packages])))


if __name__ == "__main__":
    main()
