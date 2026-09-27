"""Apply the reviewable public snapshot import extension to the pinned Mortal source."""
from pathlib import Path
import argparse
import subprocess
import shutil
from prepare import LOCK, HERE

def patch(source):
    source = Path(source).resolve()
    head = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if head != LOCK["source_commit"]:
        raise ValueError("MORTAL_SOURCE_VERSION")
    mod = source / "libriichi/src/state/mod.rs"
    original = subprocess.check_output(["git", "-C", str(source), "show", "HEAD:libriichi/src/state/mod.rs"]).decode()
    new = original + "\nmod mjcn_public;\n"
    if mod.read_text() not in (original, new):
        raise ValueError("MORTAL_SOURCE_ALREADY_MODIFIED")
    mod.write_text(new, encoding="utf-8")
    shutil.copy2(HERE / "native/mjcn_public.rs", mod.with_name("mjcn_public.rs"))

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    patch(parser.parse_args().source)
