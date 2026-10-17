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

The same build runs from the command line, with the Unity editor closed:

```bash
"C:/Program Files/Unity/Editor/Unity.exe" -batchmode -quit -projectPath Unity/LosketShaders -executeMethod Losket.EditorTools.LosketBundleBuilder.BuildBundle -logFile unity-build.log
```

The exit code is 0 even when a shader fails to compile: look for the
`[Losket] bundle ecrit` line in the log.

### Previewing the pattern without the game

```bash
python Tools/render_pattern.py out
```

Renders the blotch pattern and the exhaust spot to PNG files, from a Python
port of the shader's noise and colour maths (needs numpy and Pillow). It is a
way to judge a pattern setting in seconds; it has no lighting and no
directional mask, so the game remains the final judge. Any change to the
shader's noise has to be mirrored in the script.

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

**Two overlay shaders.** The deposit maths live in `LosketBurnOverlay.cginc`,
shared by `Losket/BurnOverlay` (a `ForwardBase` pass only: alpha blend over
the lit image) and `Losket/BurnOverlayDeferred` (a `Deferred` G-buffer pass
plus the same forward pass as fallback). `LosketOverlayRig.PickShader`
chooses per overlay from the *base* material: the deferred variant only when
that material has a pass named `DEFERRED`, which is what surface shaders and
the Standard shader generate, hence what Deferred's replacement shaders and
TexturesUnlimited 1.6 carry. Unity 2019.4 exposes pass names at runtime
(`Material.FindPass`) but not pass tags. The rule matters because, on a
deferred camera, Unity draws objects whose shader has no deferred pass in
forward, *after* the deferred lighting: a G-buffer decal on such a part
(TexturesUnlimited 1.5, Shaddy) is simply painted over, which is how the
TURD Mk3 lost all its marks. The rig re-checks the base shader every frame
and swaps the overlay's shader if it changed. Without the Deferred mod the
camera is forward and both variants render the same pass.

**G-buffer pass.** Unity picks it on cameras in deferred shading (the Deferred
mod switches the flight, editor and IVA cameras to it) when the overlay's
render queue is opaque (2500 or below), and the forward pass otherwise: a
transparent base part pushes the overlay queue past 2500. The pass writes
only RT0 (albedo, alpha blend, occlusion kept) and RT1 (specular multiplied
by `1 - coverage`, smoothness likewise), and masks RT2 (normals) and RT3
(emission), so the deposit is lit by Deferred with the part's own normal map
and shadows. Per-target blend states need Direct3D11 or OpenGL 4.0+.
Deferred's replacement shaders zero the SH ambient in their deferred pass and
compute ambient later from the G-buffer, which is why the pass does not touch
RT3. The in-flight diagnostic (`DumpRenderState`, developer sliders option)
logs the camera's actual rendering path and each overlay's shader.
