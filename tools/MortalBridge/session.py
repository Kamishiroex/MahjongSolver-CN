"""Plugin-owned launcher for existing, hash-verified personal Mortal packages.

Warm-up uses zero feature tensors, never a fabricated game or a game decision.
Keep this resource in the plugin so an existing model package needs no changes.
"""
import json
import sys
from pathlib import Path

root = Path(sys.argv[1]).resolve()
sys.path.insert(0, str(root))
from snapshot import infer
from runtime import load_engine
from prepare import LOCK

engine = load_engine(root)
import numpy as np

obs = np.zeros(LOCK["observation_shape"], dtype=np.float32)
mask = np.zeros(LOCK["action_space"], dtype=np.bool_)
mask[0] = True
engine.react_batch([obs], [mask], None)

for line in iter(lambda: sys.stdin.readline(1048578), ""):
    if len(line) > 1048576:
        raise ValueError("MORTAL_INPUT_LIMIT")
    request = json.loads(line)
    if request.get("type") == "warmup":
        response = {"schema": 1, "type": "ready", "id": request["id"],
                    "engine_commit": LOCK["source_commit"]}
    else:
        try:
            candidates, summary, warnings = infer(engine, request["snapshot"])
            response = {"schema": 1, "input_sha256": request["input_sha256"], "candidates": candidates,
                        "applied": summary, "assumptions": warnings, "engine_commit": LOCK["source_commit"]}
        except Exception as exc:
            response = {"schema": 1, "input_sha256": request.get("input_sha256"), "error": str(exc)[:600]}
    print(json.dumps(response, ensure_ascii=True, allow_nan=False), flush=True)
