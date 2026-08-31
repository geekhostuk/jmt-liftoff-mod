# Versioning and releases

## Where the version lives

In exactly one place — `<Version>` in [`Directory.Build.props`](../Directory.Build.props)
at the repo root:

```xml
<PropertyGroup>
  <Version>1.0.0</Version>
</PropertyGroup>
```

Everything else derives from it. The `GenerateBuildInfo` target in
`src/JmtLiftoffMod/JmtLiftoffMod.csproj` writes `obj/…/BuildInfo.g.cs` before each
compile:

```csharp
namespace JmtLiftoffMod;

internal static class BuildInfo
{
    public const string Version = "1.0.0";
    public const string Marker  = "1.0.0+local";
}
```

and the plugin's identity constants read straight from it:

```csharp
public const string PluginVersion = BuildInfo.Version;
public const string BuildMarker   = BuildInfo.Marker;
```

So the version reaches three places at once: the `[BepInPlugin]` attribute (what
BepInEx logs on load), the assembly metadata, and the `session_started` event the
bot sends the competition server.

> Never edit `BuildInfo.g.cs` — it is regenerated on every build and gitignored.

## The build marker

`BuildMarker` is `<Version>+<BuildRevision>`. `BuildRevision` defaults to `local`,
so a developer build reports `1.0.0+local`. CI passes the short commit SHA:

```
dotnet build … -p:BuildRevision=a1b2c3d      →  1.0.0+a1b2c3d
```

That means any DLL in the wild can be traced back to the exact commit it came
from, via the `buildMarker` field in `session_started`.

## Scheme

[Semantic versioning](https://semver.org/), from the perspective of a server
operator:

| Bump | When |
|---|---|
| **Major** | The wire protocol changes incompatibly, or the plugin GUID / config keys change. |
| **Minor** | New events, commands, or telemetry fields; new config keys with safe defaults. |
| **Patch** | Fixes and internal changes with no protocol impact. |

## Cutting a release

1. Bump `<Version>` in `Directory.Build.props`.
2. Add the entry to [`CHANGELOG.md`](../CHANGELOG.md).
3. Commit, then tag with a `v` prefix and push the tag:

   ```bash
   git commit -am "Release 1.1.0"
   git tag v1.1.0
   git push origin main --tags
   ```

The [`release` workflow](../.github/workflows/release.yml) fires on the tag, checks
that the tag matches `<Version>` (and fails loudly if it doesn't), and publishes a
GitHub release with notes.

### Release assets

Building the DLL requires Liftoff's copyrighted managed assemblies, which cannot be
committed to this repo or shipped to GitHub-hosted CI. So the release workflow
publishes the release and its notes, and the **binary is built on a machine that
owns a copy of the game** and uploaded to that release:

```bash
git checkout v1.1.0
scripts/release-dll.sh
```

The script refuses to run on a dirty tree, or when `HEAD` is not the tagged commit
— otherwise the `buildMarker` compiled into the DLL would point at a commit the
release doesn't contain. It builds with `BuildRevision` set to the short SHA,
packages the DLL, and uploads three assets:

| Asset | Contents |
|---|---|
| `JmtLiftoffMod.dll` | The plugin, for dropping straight into `BepInEx\plugins\`. |
| `JmtLiftoffMod-<version>.zip` | DLL + pdb + `LICENSE` + install instructions. |
| `SHA256SUMS.txt` | Checksums for both of the above. |

Pass `--no-upload` to build and package without touching the release.

There is a gap between the workflow publishing the release and the script
attaching the binary. If that matters to you, have the workflow create the release
as a draft (`gh release create --draft`) and publish it after the upload.

### Why CI doesn't build it

A GitHub-hosted runner has no copy of Liftoff, and the game's assemblies must not
be committed here. A self-hosted runner with the game installed *would* work, but
attaching one to a **public** repository is a known security hazard — any fork's
pull request could run code on that machine. Building locally and uploading is the
safer trade.
