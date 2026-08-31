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

### About release assets

Building the DLL requires Liftoff's copyrighted managed assemblies, which cannot
be committed to this repo or shipped to CI. **The release workflow therefore
publishes source only** — it does not attach a compiled DLL.

To ship a binary, build it on a machine with Liftoff installed and attach it to
the published release:

```powershell
dotnet build src\JmtLiftoffMod\JmtLiftoffMod.csproj -c Release -p:BuildRevision=$(git rev-parse --short HEAD)
gh release upload v1.1.0 src\JmtLiftoffMod\bin\Release\net472\JmtLiftoffMod.dll
```

If you later put the reference assemblies somewhere CI can legally reach them
(a private repo or a self-hosted Windows runner with the game installed), the
commented-out `build` job in the release workflow shows what to enable.
