# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project follows
[Semantic Versioning](https://semver.org/) — see [docs/versioning.md](docs/versioning.md).

## [1.0.0] — 2026-08-31

First release of **JMT Liftoff Mod** as a standalone project. Extracted from
`LiftoffRaceBot` in [geekhostuk/liftoff-bots](https://github.com/geekhostuk/liftoff-bots)
and rebranded.

### Changed

- **Rebranded.** Plugin GUID is now `uk.co.geekhost.jmtliftoffmod` (was
  `uk.co.geekhost.liftoff.racebot`), display name `JMT Liftoff Mod`, assembly and
  DLL `JmtLiftoffMod.dll`, namespace `JmtLiftoffMod`. The config file and
  data/log folder move with the GUID. **This is not a drop-in upgrade** — see the
  upgrade table in [docs/install.md](docs/install.md#upgrading-from-liftoffracebot).
- **Version is now a real build input.** `<Version>` in `Directory.Build.props` is
  the single source of truth; a `GenerateBuildInfo` MSBuild target generates the
  `PluginVersion` and `BuildMarker` constants from it, so the `[BepInPlugin]`
  attribute, the assembly metadata and the `session_started` event can no longer
  drift apart. The previously hand-edited `BuildMarker` string is replaced by
  `<version>+<revision>`, where CI supplies the commit SHA.
- Repo layout flattened to a single plugin: `plugins/LiftoffRaceBot/` →
  `src/JmtLiftoffMod/`.
- Documentation rewritten for one plugin — the LobbyBot comparison material is
  gone, and versioning/release process is documented in
  [docs/versioning.md](docs/versioning.md).

### Added

- GitHub Actions workflows: `ci` validates the version/tag/changelog wiring on
  every push, `release` publishes a GitHub release when a `v*` tag is pushed.

### Notes

No behavioural changes to telemetry, the track-control layer, or the server
protocol — the wire vocabulary in [`contracts/`](contracts/) is unchanged from
`LiftoffRaceBot`, other than the plugin name reported in `session_started`.

[1.0.0]: https://github.com/geekhostuk/jmt-liftoff-mod/releases/tag/v1.0.0
