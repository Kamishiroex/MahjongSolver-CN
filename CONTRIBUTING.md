# Maintenance

The CN build entry is `scripts/build-cn.ps1`; see [README-CN.md](README-CN.md) for pinned dependencies. Only reused upstream gameplay modules and relevant regressions are retained; the independent international plugin shell, telemetry deployment and tuner are excluded.

- Preserve tests for reads, model protocols, action dispatch, lifecycle and public-state provenance. Synthetic fixtures and captured public data must remain distinguished; neither alone proves live compatibility.
- Keep licenses, attribution, dependency locks, resources, native adapter/patch sources and corresponding source for distributed binaries.
- Put captures, credentials, personal AI installations and output under ignored local directories. Review fixtures: no player/account names, machine paths, raw memory or actual credentials.
- Maintain `scripts/source-files.txt` as an explicit reviewed package list. Formal packaging requires a clean commit, validates source identity before/after the build and rebuilds the source ZIP. Use `-VerifyOnly` while editing.
- Run the build suites, `python scripts/check-doc-links.py` and `python -m unittest discover -s tests -p test_source_package.py` when changing packaging/docs. Never delete failing tests to make a release pass.
- Inspect package entries, checksums and `artifacts/build-manifest.json`. Public reports must not contain local paths, machine names or secrets. Do not commit private audit reports.

Display branding lives in `Mahjong.Plugin.CN/Brand.cs`. Keep the CN plugin manifest, `repo.json`, project description and user documentation consistent with it. Display-name changes must preserve `Mahjong.Plugin.CN`, `/mjcn`, `###mahjong-cn`, existing control IDs, configuration and release paths. Leave upstream names, licenses and historical release notes intact. Use the [future release template](docs/cn/RELEASE-TEMPLATE.md) with actual version and verification results; a local branding build is not a published update.

Compatibility evidence is retained in [VERSION-EVIDENCE.md](docs/cn/VERSION-EVIDENCE.md), field-specific notes and reviewed fixtures. Known limitations remain in [PUBLIC-INPUT-GAPS-20260925.md](docs/cn/PUBLIC-INPUT-GAPS-20260925.md). Build success is not CN live acceptance.

The solution keeps its historical filename because test fixtures use it as a root marker; its active projects are the CN plugin and shared libraries/tests. Optional local community-service source is retained for the implemented opt-in client; no public service is deployed.
