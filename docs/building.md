# Building from source

## Requirements

- **.NET SDK** 8.0 or later (the project targets `net472`; any modern SDK builds it).
- A **Steam copy of Liftoff** installed — the project references the game's managed
  DLLs and BepInEx's core DLLs directly from your install. **No game or BepInEx
  assemblies are committed to this repository**, and none may ever be: they are
  copyrighted. `<Private>false</Private>` on every reference ensures they are never
  copied into build output either.
- **BepInEx 5.x** present in the Liftoff folder (an inactive install renamed to
  `_BepInEx` is also detected).

## Environment variables

| Variable | Default / detection | Purpose |
|---|---|---|
| `LIFTOFF_DIR` | `C:\Program Files (x86)\Steam\steamapps\common\Liftoff` | Liftoff install folder (containing `Liftoff.exe`). |
| `BEPINEX_CORE_DIR` | Auto-probed: `$(LIFTOFF_DIR)\BepInEx\core`, then `$(LIFTOFF_DIR)\_BepInEx\core` | Folder containing `BepInEx.dll` and `0Harmony.dll`. Set explicitly to override. |

Set them only if your paths differ from the defaults:

```powershell
$env:LIFTOFF_DIR = 'D:\SteamLibrary\steamapps\common\Liftoff'
```

## Build

```powershell
dotnet build JmtLiftoffMod.slnx -c Release
```

Or the project directly:

```powershell
dotnet build src\JmtLiftoffMod\JmtLiftoffMod.csproj -c Release
```

Output:

```
src\JmtLiftoffMod\bin\Release\net472\JmtLiftoffMod.dll
```

The output folder contains only the plugin DLL (+ pdb) — deploy just the DLL into
`Liftoff\BepInEx\plugins\` ([install.md](install.md)).

To stamp a build with its commit, pass `BuildRevision` (CI does this
automatically — see [versioning.md](versioning.md)):

```powershell
dotnet build src\JmtLiftoffMod\JmtLiftoffMod.csproj -c Release -p:BuildRevision=a1b2c3d
```

If the build warns `BepInEx not found at ''…`, neither probe path matched — set
`BEPINEX_CORE_DIR` to your BepInEx `core` folder.

## Notes

- The build is `Deterministic` and nullable-annotated (`<Nullable>enable</Nullable>`).
- The project compiles against whatever game version is installed. The plugin
  reaches game internals via reflection and Harmony with soft-fail lookups, so
  minor game updates usually don't require a rebuild — but the reverse-engineered
  track-control layer should be re-validated in-game after any Liftoff update
  (see [multiplayer-track-control.md](multiplayer-track-control.md)).
- `obj/…/BuildInfo.g.cs` is generated on every build and is not committed.
