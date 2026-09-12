# Losket

KSP 1.12.5 mod adding persistent marks on parts: atmospheric reentry burns and
dust kicked up by engines.

## Environment

To point to a KSP install, create a `LocalSettings.props` at the
root (git-ignored):

```xml
<Project>
  <PropertyGroup>
    <KSPRoot>D:\My\Other\KSP</KSPRoot>
  </PropertyGroup>
</Project>
```

## Directory layout

```
Losket/
├─ Source/Losket/        plugin C# code
├─ Unity/LosketShaders/  Unity project, only used to compile the shaders
├─ GameData/Losket/      what gets copied into KSP (build output included)
├─ Tools/                build and log-reading scripts
├─ Exemples/             visual references and specifications
└─ shabby/               reference copy of the Shabby repo
```

## Development cycle

### C# code

```bash
powershell -File Tools/build.ps1 -Run
```

Compiles, copies `GameData/Losket` into the dev install, and launches KSP.
Without `-Run`, the game isn't started. The DLL is produced directly at
`GameData/Losket/Plugins/Losket.dll`.

### Shaders

1. Open `Unity/LosketShaders` in Unity 2019.4.18f1.
2. Menu **Losket > Configure Project** (only once, after first opening the
   project).
3. Menu **Losket > Compile Shader Bundle** (`Ctrl+Shift+B`).

The bundle is written to `GameData/Losket/Shaders/Losket.shaderbundle`. You
then need to rerun `Tools/build.ps1` to deploy it.

### Reading logs

```bash
powershell -File Tools/logtail.ps1
```

Filters `KSP.log` for `[Losket]` and exceptions. `-Follow` tails the file
live while the game is running.

## Technical notes

**Bundle loading.** The bundle uses the `.shaderbundle` extension instead of
`.shab`, and is loaded by `LosketBootstrap` with `AssetBundle.LoadFromFile`.
Going through Shabby is possible but creates a conflict: Shabby registers a
loader for `.shab`, and Unity refuses to have the same file loaded twice as
an AssetBundle. Loading it ourselves also removes any mandatory dependency on
Shabby.

**Do not trim `Packages/manifest.json`.** The Unity project must keep
Unity 2019.4's default manifest and its 38 modules. A trimmed manifest
produces an AssetBundle that looks perfectly valid — correct UnityFS header,
serialization version 21, `StandaloneWindows64` target, same external
dependencies as a working bundle — which KSP then rejects with a misleading
message:

> The AssetBundle … could not be loaded because it is not compatible with this
> newer version of the Unity runtime.

The critical module is `com.unity.modules.assetbundle`. `BuildBundle` now
checks for its presence before compiling and refuses to produce a bundle
without it. No symptom shows up on the Unity side: the project opens, the
shader compiles, the build succeeds.

**Color space.** The Unity project's `PlayerSettings.colorSpace` must match
KSP's. KSP 1.12.5 runs in **Gamma** (verified in the startup log), and the
project is set accordingly. The actual value is logged on every launch by
`LosketBootstrap`.

**Where to read the logs.** KSP writes to `KSP.log` at the game's root *and*
to `%USERPROFILE%\AppData\LocalLow\Squad\Kerbal Space Program\Player.log`.
The former can stay stuck on an old session; `Tools/logtail.ps1`
automatically picks whichever of the two is most recent and shows which one
it picked. As long as the game holds a log file open, neither its size nor
its directory entry date get refreshed — you need to read the stream, not
the metadata.

**Graphics APIs.** The bundle is compiled for Direct3D11 and OpenGLCore.
Without OpenGLCore, shaders appear magenta for players launching KSP with
`-force-glcore`.
