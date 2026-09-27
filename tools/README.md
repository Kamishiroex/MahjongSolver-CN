# Maintenance tools

- `Mahjong.Cn.*` and `Mahjong.Akochan.Replay`: engine import, public-state probes and journal replay. Several are used by the build; source and locks are required.
- `MortalBridge`: pinned runtime/model preparation, native adapters, public snapshot bridge and tests. `session.py` is embedded into `Mahjong.Cn.Core` for prewarming: a runtime dependency, not temporary output.
- `extract-fixture.mjs` / `test-extract-fixture.mjs`: upstream replay fixture extraction and synthetic self-test. Review real input before sharing.
- `scan_tiles.py`, `diff_nodes.py`, `analyze_snaps.py`: generic offline upstream layout maintenance, not proof of CN offsets. Private inputs stay outside the public tree.

CN analyzers and native akochan patch/build scripts live in `scripts/`. Do not commit output, personal model bundles, telemetry corpora or credentials. See [maintenance rules](../CONTRIBUTING.md).

- `UiRenderHost`: runs the production ImGui window with synthetic state and no game services. Requires the pinned local Dalamud DLLs and Windows font; neither is redistributed. `dotnet run --project tools/UiRenderHost -c Release -- .work/ui-host` renders the five pages and size/scale matrix. Its test-only assembly name grants internals access; **never install its DLL as a plugin**. PNGs are clearly labelled test host, not live CN proof. Software font sampling differs from the game's GPU renderer.
- `CommunityStats`: optional, undeployed local protocol host. `dotnet run --project tools/CommunityStats -c Release` listens only at `http://127.0.0.1:57831`. Supports GET public count, POST random-session heartbeat, DELETE session. Only the single `sessionId` field is accepted. Records expire after 180 seconds of server monotonic time, repeat heartbeats within 30 seconds return 429, input and session count are bounded. No accounts, scores, logs or persistent storage. Public hosting, abuse mitigation, CDN/access-log retention and TLS need a separate deployment review; this is not a deployed online service.
