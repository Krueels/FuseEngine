# PROJECT.md — FuseEngine

## Purpose of this document

This file is the operational map of the repository for anyone that needs to modify the project. It describes the current architecture, the contracts between editor and game, persisted formats, render order, pause rules, known risks, and the purpose of every C# file.

Use the paths in this document relative to the repository root:

`C:\Users\niko\HDD2\DEV\Csharp\FuseEngine`

State verified on **2026-09-04**. The repository contains the current working-tree implementation of the procedural-terrain, terrain-material-graph, procedural-staircase, and procedural-grass plans in `PLAN.md`; do not assume the checkout is clean or discard uncommitted changes. There is no automated test project.

## Rules for editing safely

1. Read this file and `README.md` before deciding where to make a change.
2. Check Git status before editing and preserve existing changes; do not use destructive operations to “clean” the repository.
3. First determine whether the change belongs to the runtime (`Fuse`), the editor (`Blowtorch`), or both. The editor references the `Fuse` project, so a runtime API change can break the editor.
4. Preserve compatibility of the `.bth`, `.terrain`, `.fmat`, and `.fgeo` formats. When changing serialization, read old files and account for missing values, versions, and legacy paths.
5. OpenGL calls, GPU buffer/texture creation, and GPU uploads must happen on the rendering context/thread. Do not move CPU loading to the GL thread without understanding the upload flow.
6. Use the existing resource resolvers (`ResPath` and `Bible`) instead of building paths from the current working directory. Runtime assets are relative to `Fuse/res`.
7. When changing a render stage, preserve OpenGL state, framebuffer attachments, HDR/linear format, and the depth/blend/cull state expected by later stages.
8. Before creating a second calculation for water, waterline, clouds, or lighting, look for the existing shared calculation. Water already publishes surface data for underwater effects; clouds and ocean receive simulation time from the runtime.
9. The editor’s Play mode must continue using the runtime copied by the post-build target in `Blowtorch.csproj`. Changes to the `Fuse` executable must be built before validating F5 in the editor.
10. Build both projects after a relevant change. There is no automated test suite; visual validation in the editor/game and log inspection are part of the process.

## Repository structure

```text
FuseEngine/
├── FuseEngine.slnx        solution containing both projects
├── Fuse/                   executable game/runtime
│   ├── Program.cs
│   ├── src/                engine, renderer, scene, physics, and gameplay code
│   └── res/                shaders, textures, models, maps, audio, and fonts
├── Blowtorch/              map editor built on top of the runtime
│   └── *.cs
├── README.md               overview and controls
├── imgui.ini               persisted ImGui layout
└── .gitignore              generated outputs and local directories
```

There is no `Blowtorch/res`: the editor uses resources from `Fuse/res` and the configured runtime path. Project `bin/` and `obj/` directories are generated outputs and are ignored by Git.

## Projects, build, and execution

### Solution

`FuseEngine.slnx` includes:

- `Fuse/Fuse.csproj`
- `Blowtorch/Blowtorch.csproj`

Both are `net10.0-windows` executable projects, use nullable reference types, implicit usings, and `unsafe` code. The editor also uses Windows Forms for the application window/shell.

### Main dependencies

`Fuse/Fuse.csproj` uses:

- Silk.NET OpenGL and GLFW `2.22.0` for the context, window, input, and OpenGL API;
- ImGui.NET `1.91.6.1` for UI/debug;
- JoltPhysicsSharp `2.21.0` for physics and queries;
- Assimp through Silk.NET.Assimp `2.22.0` for model importing;
- MIConvexHull `1.1.19.1019` for convex hulls;
- SoLoudSharp `0.2.0` for audio;
- StbImageSharp `2.30.15`, StbTrueTypeSharp `1.26.13`, and TinyEXR for images, fonts, and EXR.

`Blowtorch/Blowtorch.csproj` references `Fuse/Fuse.csproj` and uses the same OpenGL, GLFW, ImGui, Jolt, Assimp, image, and font packages. It also declares the `blowtorch.ico` icon and a `UserSecretsId`.

### Useful commands

Run from the repository root:

```powershell
dotnet restore FuseEngine.slnx
dotnet build FuseEngine.slnx --no-restore
dotnet run --project Fuse/Fuse.csproj -- default.bth
dotnet run --project Blowtorch/Blowtorch.csproj
```

The `CopyFuseRuntimeToBlowtorch` target copies the required artifacts from `Fuse/bin/...` to `Blowtorch/bin/...`, allowing the editor’s Play/F5 command to start the game. If the editor is running an old runtime, build `Fuse` and then `Blowtorch` again.

## Runtime input and lifecycle

The entry point is `Fuse/Program.cs`. It creates `Application`, initializes the window, assets, renderer, physics, scene, player, enemies, audio, and HUD, loads the initial map, and enters the loop.

`Fuse/src/Core/Application.cs` is the frame orchestrator:

1. processes events/input and map/reload requests;
2. toggles pause with `Esc`, locks/unlocks the cursor, and pauses audio;
3. records render-frame timing with `Engine.Tick(dt, false)`;
4. advances `Engine.Time` and runs water buoyancy/physics once per fixed `1/60` step, capped at 8 steps per frame;
5. continues rendering the image while the game is paused;
6. draws the HUD, console, debug views, and post-processing.

`Fuse/src/Core/Engine.cs` stores `DeltaTime`, FPS, and `Time`. Runtime simulation time advances through `AdvanceSimulation(fixedDeltaTime)`, not directly at render frequency, so rigid bodies and the visible ocean share the same fixed-step clock. This differs from the wall-clock timer used as a fallback by some renderers.

### Pause: current contract

- Physics, behaviours, animations, and gameplay must not advance while `_paused` is true.
- Ocean buoyancy, player water movement, and the ocean simulation must not advance while `_paused` is true.
- The frame continues to be drawn so the screen and UI remain responsive.
- Ocean and clouds receive `Engine.Time` in the game path and should therefore freeze their simulation animation during pause.
- `VolumetricFogRenderer` currently calculates time directly from `Environment.TickCount64`; it does not receive `Engine.Time` through the same contract. This is a known limitation: if fog animation must pause, introduce explicit simulation time without creating another parallel clock.
- The editor intentionally omits the optional simulation time so its preview keeps moving while the editor is open.

## Blowtorch editor architecture

`Blowtorch/Program.cs` starts `EditorApplication`. The editor maintains an editable `MapDocument` and a synchronized runtime `Renderer.Scene` for preview. The main services are:

- `EditorSceneService`: loads/saves the document, builds the runtime, synchronizes objects, terrains, neighbors, and global settings;
- `EditorAssetService`: catalogs, imports, reloads, removes, and resolves assets from `Fuse/res`;
- `EditorViewport` and `ViewportCamera`: viewport, cameras, picking, gizmos, and rendering;
- `EditorUI`: menus, inspector, terrain creation/sculpt/neighbors, skybox, clouds, fog, ocean, materials, and hierarchy;
- `EditorLightingSystem`: editor light visualization and shadow caches;
- `AssetBrowserWindow`: asset selection, inspection, and operations;
- `UndoManager` and `CommandHistory`: document/terrain snapshots and undo/redo.

The editor has two concepts that must not be confused:

- **document**: editable, serializable data, mainly in `MapDocument` and assets;
- **runtime preview**: entities, meshes, materials, Jolt bodies, and render targets created to visualize the document.

Changing only the runtime scene can make a change disappear after reload; changing only the document can leave the preview stale. Editor changes usually need to update both sides and mark the document as modified.

## Rendering pipeline

`Fuse/src/Renderer/MasterRenderer.cs` is the central coordinator. The current relevant order is:

1. updates transforms and terrain LOD and prepares casters;
2. selects lights, updates sky/IBL, and updates cloud shadows;
3. renders directional/cascade, spot, and point-light shadows when enabled or required by shafts/fog;
4. uploads data to `LightingBuffer` and performs Forward+ culling;
5. renders the skybox into the HDR framebuffer;
6. renders static objects, skinned objects, and decals;
7. renders the ocean, copying scene/depth when needed and publishing the surface sidecar;
8. renders/composites clouds, except when the underwater state requires composition to be deferred;
9. renders/composites volumetric fog;
10. applies the pending ocean underwater pass after fog;
11. draws billboards, HUD/overlays, and runs the post-process pipeline;
12. presents the result in the window.

The main intermediate format is HDR/linear. Bloom, SSAO, motion blur, tonemapping, and compositing may use framebuffer pools and different resolutions. When adding a pass, restore framebuffer bindings, viewport, depth test, blend, culling, draw buffers, and active textures.

### Lighting

- `Light` supports `Directional`, `Point`, and `Spot`.
- `LightingBuffer` publishes the directional light and local-light arrays, with current conventional shader limits of 8 point lights and 4 spot lights.
- `ForwardPlusLighting` culls local lights per tile and exposes them through SSBO/lists to the forward pass.
- `ShadowMap` handles 2D directional/spot shadows; `PointShadowMap` handles point-light cubemaps.
- `MasterRenderer` maintains shadow caches and invalidates/updates them based on light position, revision, and settings.
- Volumetric point/spot lighting uses the renderer’s light data and shadow maps. Do not create a second “helper” light only for fog: that produces duplicate halos and divergence between visible and volumetric lighting.

### Skybox and IBL

`ProceduralSky` and `Shaders/skybox*` generate the day/night sky, horizon, stars, and sun. The sun direction follows the first enabled directional light in the map. The procedural sky also feeds environment/IBL generation used by materials.

### Volumetric clouds

`VolumetricCloudRenderer` and `Shaders/Clouds/*` currently implement:

- a procedural spherical shell adapted to scene scale;
- a 128³ Perlin-Worley noise volume and smaller Worley detail;
- a 1024² weather map and profile/atmosphere LUT;
- separate coverage, shape, and erosion controls, with Stratus, Stratocumulus, and Cumulus presets;
- domain warping to reduce tiling;
- primary raymarching and cone lighting;
- cloud shadows, multi-scattering, and ambient contribution;
- reduced-resolution rendering, temporal reprojection/history, and depth-aware upscale;
- adaptive raymarching to control cost.

In the game path, `MasterRenderer` sends `Engine.Time` to `UpdateShadow` and `Render`. In the editor, the parameter is omitted so the preview keeps moving. The renderer has an `Environment.TickCount64` fallback; it must not be used in the game path when pause behavior is required.

### Volumetric fog

`VolumetricFogRenderer` performs height-aware low-resolution fog with noise, absorption, anisotropy, shafts, sun and local-light illumination, shadow maps, temporal history, and fullscreen composition. Fog is rendered over the scene and skybox. Its current time still comes from its internal wall-clock path, so pause/time changes must be handled carefully.

### Procedural grass

`ProceduralGrassPatchSet` follows the resident tiles of each `ProceduralTerrainLayer`. It generates deterministic candidates on worker tasks, applies terrain height/slope/water/biome rules and sparse painted masks, and keeps only a bounded patch window around the camera. Candidate height and slope use `TerrainAsset.GetTriangulatedSurfaceHeight`, matching the two-triangle diagonal used by the rendered terrain mesh instead of the separate bilinear query. The desired patch set is reused while the camera remains within half a patch and is rebuilt only after meaningful movement, profile changes, or a terrain-streaming revision. `ProceduralGrassRenderer` uploads candidates to SSBOs and runs compute culling for distance/density/frustum. LOD 0 uses two curved crossed quads per blade, LOD 1 groups two spatially distributed blades into one upright clump instance, and LOD 2 groups a spatial 2x2 cell block into four single upright ribbons; the grouping reduces instance writes and far-field overdraw without creating the visible stripes caused by selecting every Nth flattened candidate. The vertex and shadow shaders project LOD clump offsets onto the candidate's terrain tangent plane so distant ribbons do not float or cut through sloped terrain. Species are selected deterministically and encoded in the instance data, so adding species does not create one entity or one draw call per species. Only LOD 0 participates in the near shadow pass.

On drivers that accept `DrawElementsIndirect`, the three LODs use GPU-written indirect commands. Intel's P4600 OpenGL driver rejects that draw path, so the renderer keeps GPU culling enabled and falls back to `DrawElementsInstanced`. That fallback uses rotating pixel-pack readback buffers plus GL fences, polling only completed results instead of synchronously reading counters every frame. It also ping-pongs the compacted LOD instance buffers: the last completed set remains on screen while the next cull is being written, and the sets are swapped only after its counter readback completes. This avoids flashing caused by drawing a partially written list with an old count. A residency upload does not invalidate an in-flight fence or clear the active counts; fallback culls are serialized until the current fence completes, and monotonic culling generations reject results older than the active one. The last completed draw list remains visible during the short replacement interval and is then atomically replaced, so a patch upload cannot create a full-frame hole or starve the renderer while patches stream in. `uCameraDelta` keeps the active list camera-relative between culls. The grass fragment pass has no blending, uses alpha-test discard and early depth tests, and keeps the expensive diagnostic patch readback opt-in.

Hi-Z patch occlusion is optional and disabled by default in `ProceduralGrassSettings`. It builds a max-reduced depth pyramid from the game renderer's previous-frame `HdrDepthId` or from the editor viewport's current depth texture, then culls whole patches conservatively; the first frame after initialization or a resize only seeds the history. The setting has a bias to avoid false occlusion and should be profiled on low-end hardware. `F9` enables the Debug Drawer, LOD colors, and the expensive GPU readback that reports frustum/distance/density/Hi-Z elimination; this is a diagnostic mode, not a shipping performance path. The grass profile and sparse density-mask edits are exposed by `Blowtorch/EditorUI.cs`, while the renderer is called from both `EditorViewport` and `MasterRenderer`.

### Ocean and underwater

`OceanRenderer` owns the visual three-cascade spectral/FFT ocean, camera-following adaptive mesh, compute simulation/resolve, detail normal map `res/Textures/ocean_normal.png`, reflection/refraction/foam, and the surface/depth sidecar for underwater composition. `OceanSurfaceSampler` is the shared CPU representation of the same H0 spectrum, dispersion, displacement, normal, and wave velocity used by waterline and physics queries; physics keeps a 20-cell reduced frequency band so sub-collider ripples do not excite rigid-body torque, but it does not quantize position or fixed-step time. Wave velocity includes `WaveSpeed`, matching the visible phase derivative. `OceanPhysicsSystem` clips each supported convex collider against the local tangent plane of that shared wave, computes the submerged volume and centroid, applies Archimedean buoyancy at the centroid, and applies separate water-relative linear/angular drag. Boxes use their exact six-face volume, spheres use an analytic spherical-cap solution, capsules use a cached convex tessellation, dynamic mesh bodies rebuild the convex-hull topology used by Jolt, and grouped bodies use one cached aggregate compound geometry. Its runtime path culls only bodies certainly above the wave envelope, continues applying buoyancy at every depth, reuses geometry/scratch buffers, and collects center-of-mass/center-of-buoyancy diagnostics only when F9 is enabled. Water forces use the collider's current fixed-step pose rather than a predicted pose. Aggregate linear drag is central because the volume approximation does not provide a center of pressure; only buoyancy at the displaced centroid produces righting torque. Rotational drag is integrated against Jolt's world inverse inertia so it cannot flip angular velocity in one step. At the free surface, effective drag area grows smoothly from zero to the exact fully wetted projection, and quadratic drag uses a stable analytic average. `MapBody` can optionally persist a custom `buoyancy_volume`; absent that value, the collider volume is used. The game passes fixed-step `Engine.Time`; the editor leaves optional simulation time absent so its preview keeps moving. `OceanSettings` persists the physics toggle and all water/player tuning values in `.bth` maps.

Do not implement the wave height again in the underwater shader. The historically sensitive ocean bug was evaluating the wave in two different ways; the surface and waterline test must share the same space convention, time, spectrum, and displacement.

## Scene system, maps, and persisted assets

### `.bth`

`.bth` maps are current/legacy JSON documents. `MapSerializer` is the runtime path for saving/loading `Renderer.Scene`, Jolt bodies, player spawn, skybox, clouds, fog, ocean, brushes, models, materials, lights, decals, interactions, and behaviours. A visible hierarchy node with a non-empty collider and descendants is loaded as a Jolt compound body: descendants keep their render entities and authored body data but do not get independent runtime bodies. `MapShapeType.Compound` is an explicit persisted shape, while old group colliders authored as `box` remain compatible and are upgraded to compounds when they have collidable descendants. A map object may also contain a `staircase` JSON object with `step_height`, `step_count`, and `direction`; the loader regenerates its visual staircase and one-box-per-step compound collider from the object's serialized box half-extents. Files without this marker are unchanged. `MapDocument` is the editor-oriented DOM with validation and warnings.

Do not rename serialized properties without compatibility handling. When adding a setting, use safe defaults for old maps and update clone/parse/serialize, the editor inspector, and runtime application.

### `.terrain`

- `TerrainAsset` implements the v1 binary tile: dimensions, cell size, height scale/offset, and `ushort` samples.
- `TerrainTileSetAsset` implements v2: multiple tiles connected by coordinates, still stored in a single `.terrain` file; it remains compatible with v1 tile loading.
- `ProceduralTerrainAsset` implements v3: a compact procedural recipe plus sparse sculpt deltas. The nested `ProceduralTerrainSettings` recipe is currently v5 and keeps old v1-v4 settings readable. It does not store an 80,000 km heightmap.
- `ProceduralTerrainGenerator` deterministically generates tile samples from the recipe, seed, and 64-bit tile coordinates using double-precision world domains so shared borders match.
- `ProceduralTerrainSettings` contains world dimensions, tile resolution, macro/mountain/valley/detail/warp/erosion controls, per-layer octaves, lacunarity/gain, sea level, and editor/runtime budgets. Its nested recipe version reader keeps v1 procedural settings readable.
- `TerrainStreamer` generates requested tiles on worker tasks, cancels tiles outside the active radius, and exposes completed CPU tiles for bounded render-thread uploads.
- `ProceduralTerrainLayer` connects a procedural asset to a scene object, its local transform, material/collision settings, and streaming residency.
- `TerrainQuadTree` provides a CPU-side screen-error patch selector for global/horizon work; the current scene still uses the existing chunk LOD meshes for uploaded tiles, with cross-tile adjacency stitching already shared by streamed chunks.
- Procedural terrain creation is exposed by `Blowtorch/EditorUI.cs`. The editor materializes a small preview around tile `(0, 0)` inside the creation modal, using a separate transient scene and framebuffer rather than the main editor viewports. The preview camera supports orbit and zoom, and procedural preview chunks use `Materials/GRASS.fmat` automatically; changing the recipe does not generate the complete world.
- Runtime loads the preview tiles through `MapSerializer`, registers a `ProceduralTerrainLayer`, limits initial and streamed Jolt collision to the configured collision radius, and advances it from the fixed simulation path through `SceneManager.UpdateTerrainStreaming`. Generated mesh and Jolt collision resources are created on the engine/render thread.
- Tiles in a set must keep compatible dimensions, resolution, scale, and conventions.
- `TerrainSceneBuilder` turns the asset into render chunks, `TerrainLodSet`/meshes, and a Jolt heightfield or triangle-mesh collision.
- The editor can force LOD 0 for visual inspection only; this is editor behavior and must not leak into the game.
- `ProceduralGrassSettings` is serialized as part of the v5 recipe. It stores deterministic placement, LOD, wind, lighting, sparse density-mask, optional Hi-Z, and up to four weighted species; blade positions are never serialized.
- `ProceduralGrassPatchSet` follows resident terrain tiles, generates candidates asynchronously from tile/patch/index/seed/terrain data, samples `ProceduralGrassDensityMaskStore`, and limits residency, uploads, and worker tasks.
- `ProceduralGrassDensityMaskStore` stores sparse per-tile R8 masks under the configured resource-relative namespace. Missing mask tiles mean full density; editor painting changes only the affected tile and invalidates the matching grass patches.
- Neighbor creation/removal must update the tile set, physical preview, document, and undo snapshot.

### Materials, geometry, and other resources

- `.fmat`: JSON material asset, exposed parameters, and/or material graph.
- `.fgeo`: persisted geometry/geometry graph asset.
- `.comp`: composition/configuration resources used by the project.
- `.obj`, `.fbx`, `.glb`, `.gltf`, `.blend`: source/imported models.
- `.png`, `.bmp`, `.jpg`, `.jpeg`, `.dds`, `.exr`: textures, heightmaps, skyboxes, noise maps, and HDR data.
- `.wav`, `.mp3`: audio.
- `.ttf`: fonts.

`AssetManager` is the runtime GPU cache/loader; `EditorAssetService` is the editor asset catalog/controller. The same texture may be cached by normalized path; pay attention to casing, relative paths, and resource disposal.

## Important resources under `Fuse/res`

Main folders: `Audio`, `Fonts`, `Geometry`, `Maps`, `Materials`, `Models`, `Shaders`, and `Textures`.

Especially relevant files:

- `Maps/*.bth`: test and game maps (`default`, `terrain`, `pool`, `sponza`, `infinite`, among others);
- `Terrains/terrain_1.terrain`: terrain tile set used for terrain validation;
- `Textures/ocean_normal.png`: detail normal map for the ocean;
- `Textures/Skybox/*`: skyboxes and `night.jpg`;
- `Textures/Terrain/*`: 512/1K/2K heightmaps;
- `Textures/heightmap_brushes/*.exr`: sculpt brushes;
- `Geometry/GeometryGraph.fgeo`: geometry graph asset;
- `Materials/*.fmat`: project materials, including development, emissive, weapon, Sponza, wood, and reflection materials.

## Material graph contract

The `.fmat` JSON format remains version-compatible with the existing assets. The
material graph catalog now includes the original PBR nodes plus `Vector2`,
`WorldPosition`, `WorldNormal`, `Swizzle`, `Mapping`, scalar/vector math nodes,
`TerrainHeight`, `TerrainSlope`, `Noise2D`, `FBMNoise`, `DomainWarp`,
`TriplanarTexture`, `TriplanarNormal`, `Texture2DArray`, `TerrainLayer`,
`NormalBlend`, `HeightBlend`, and `TerrainLayerBlend`.

`MaterialGraphCompiler` emits a generated `EvaluateMaterial` function that
receives the fragment UV, world position, world normal, tangent, and bitangent.
World-position coordinates are therefore continuous across terrain tiles, while
the legacy UV path remains available for existing materials. World-normal output
is tagged separately so the fragment shader does not accidentally apply a
tangent-space TBN transform twice.

Ordinary textures and texture arrays share the eight sampler slots beginning at
texture unit 7. `TextureArray` assembles `sampler2DArray` resources from the
serialized layer paths, supplies fallback layers for missing images, and owns the
OpenGL upload/disposal contract. `AssetManager` caches these arrays alongside
ordinary textures; material reload/clear must keep both caches in mind.

The editor exposes the node properties and semicolon-separated layer path lists.
Its node preview is a CPU approximation for procedural/noise nodes; the actual
graph is compiled and rendered by the runtime preview material after edits.

## Shader inventory

All shaders are under `Fuse/res/Shaders`. Loading and `#include` support are handled by `Shader`; compute shaders are handled by `ComputeShader`.

| Path | Purpose |
|---|---|
| `Shaders/default.vert` / `default.frag` | Standard static geometry pass, materials, and PBR lighting. |
| `Shaders/skinned.vert` | Vertex skinning; normally shares the default fragment shader. |
| `Shaders/shadow.vert` / `shadow.frag` | Directional cascades and 2D spot shadows. |
| `Shaders/shadow_skinned.vert` | Skinned casters in the shadow pass. |
| `Shaders/point_shadow.vert` / `point_shadow.frag` | Point-light shadow cubemap. |
| `Shaders/point_shadow_skinned.vert` | Skinned casters in the point-light cubemap. |
| `Shaders/lighting.glsl` | UBOs, light data, shadows, and shared helpers. |
| `Shaders/forward_plus.comp` | Per-tile point/spot light culling. |
| `Shaders/billboard.vert` / `billboard.frag` | Icons and sprites/billboards. |
| `Shaders/decals.vert` / `decals.frag` | Decals projected onto the scene. |
| `Shaders/skybox.vert` / `skybox.frag` | Textural/procedural skybox, day/night, sun, and stars. |
| `Shaders/skybox_capture.vert` | Geometry for capturing the sky into an IBL cubemap. |
| `Shaders/skybox_common.glsl` | Shared sky functions and parameters. |
| `Shaders/Clouds/cloud_common.glsl` | Shared cloud functions, noise, profiles, and constants. |
| `Shaders/Clouds/cloud_noise.comp` | 3D noise volume generation. |
| `Shaders/Clouds/cloud_weather.comp` | Weather map generation. |
| `Shaders/Clouds/volumetric_clouds.frag` | Cloud raymarching, density, lighting, and reprojection. |
| `Shaders/Clouds/cloud_shadow.frag` | Cloud shadow map/occlusion. |
| `Shaders/Clouds/cloud_composite.frag` | Cloud temporal/upscale composition over HDR. |
| `Shaders/Fog/volumetric_fog.frag` | Height-aware fog raymarching and shafts. |
| `Shaders/Fog/volumetric_fog_composite.frag` | Fog composition into the main framebuffer. |
| `Shaders/Ocean/ocean.vert` / `ocean.frag` | Ocean mesh, displacement, shading, normal map, and surface. |
| `Shaders/Ocean/ocean_simulation.comp` | Spectral simulation step. |
| `Shaders/Ocean/ocean_spectrum.comp` | Spectrum initialization/evolution. |
| `Shaders/Ocean/ocean_fft.comp` | FFT for the cascades. |
| `Shaders/Ocean/ocean_resolve.comp` | Resolve displacement/normal/foam for rendering. |
| `Shaders/Ocean/underwater.frag` | Underwater composition based on surface/scene data. |
| `Shaders/PostProcess/composite.vert` / `composite.frag` | Fullscreen geometry and final composition. |
| `Shaders/PostProcess/common.glsl` | Shared post-process helpers. |
| `Shaders/PostProcess/bloom.glsl` | Bloom extraction, blur, and contribution. |
| `Shaders/PostProcess/ssao.glsl` / `ssaoBlur.glsl` | SSAO and occlusion-buffer blur. |
| `Shaders/PostProcess/motionBlur.glsl` | Motion blur. |
| `Shaders/PostProcess/tonemap.glsl` | HDR tonemapping for window output. |
| `Shaders/grid.vert` / `grid.frag` | Editor viewport grid. |
| `Shaders/death_screen.vert` / `death_screen.glsl` | Death-screen overlay. |

## Conventions and integration points

### Spaces, time, and camera

- `Camera` is the source of view/projection, rays, and FOV; do not derive camera position from UI values.
- Terrain, ocean, sky, clouds, and fog must agree on the vertical axis and world origin.
- `Engine.Time` is simulation time; `Environment.TickCount64` is wall-clock time and should only appear in an intentional preview/fallback path.
- Time changes should be passed as parameters, not duplicated per renderer.

### GPU resources

- `Texture`, `Mesh`, `Shader`, `ComputeShader`, `ShadowMap`, and framebuffer pools are OpenGL resource wrappers with explicit disposal.
- The asset cache and renderers assume a valid GL context during upload/draw.
- Recreating a shader or texture requires rebinding uniforms, image units, SSBO/UBO resources, and invalidating history when the format changes.
- Cloud/fog/underwater temporal histories must be invalidated when camera, resolution, scale, preset, or depth changes abruptly.

### Scene, entities, and visibility

`Renderer.Scene` contains entities, hierarchy, transforms, mesh/model/skinned/terrain data, materials, rigid bodies, attached lights, and shadow/visibility flags. `MapObject` is the corresponding serializable/editor representation. Global visibility and editor selection are not automatically equivalent to the runtime `Enabled` flag; check the synchronization path before changing either one.

### Physics

`PhysicsWorld` is based on Jolt and provides bodies, filters, stepping, and queries. `RigidBody` also builds static or mutable compound shapes from primitive, convex, and mesh child definitions. `OceanPhysicsSystem` runs before `PhysicsWorld.Step`: it samples the shared ocean spectrum at the current fixed simulation time, clips convex collider geometry against the local water plane, computes displaced volume and center of buoyancy, applies Archimedean buoyancy at that point, applies aggregate translation drag centrally, and applies inertia-bounded rotational drag separately. It then supplies the player’s submersion state to `Player`. Forces are evaluated once from the current fixed-step pose; the free-surface drag area grows continuously with submersion so entry is fluid rather than collision-like. F9 draws the true Jolt center of mass, center of buoyancy, water tangent, gravity, buoyancy, and resultant-force vectors.

## Known issues and risks

- Volumetric fog does not use the same simulation-time parameter as ocean/clouds. It may therefore continue animating while paused.
- The editor renders continuously to provide a live preview; omitted time parameters in the editor are intentional.
- The ocean has an expensive simulation pipeline with three cascades, normal map, scene/depth copies, and deferred underwater. Increasing resolution, samples, or render scale can reduce FPS sharply; measure before and after.
- Clouds use reduced rendering, temporal history, and adaptive raymarching. Changing jitter, history, depth upsample, or frame index can cause pixel artifacts, lines, ghosting, or disappearing clouds.
- Cloud composition is conditional on underwater state and the underwater pass occurs after fog; changing the order can make clouds disappear or be tinted incorrectly.
- Duplicated volumetric light usually means the shader evaluates one light twice, a shadow volume is in the wrong space, or stale light data survived a frame. Do not fix this by adding a compensating second contribution.
- Forward+ and conventional light buffers have local-light limits. Changing them requires updating C#, UBO/SSBO definitions, culling, and every shader that indexes the arrays.
- Neighboring terrains must use the same resolution/scale and coherent borders; changing only the mesh can leave collision or the `.terrain` file stale.
- `MapSerializer` maintains compatibility with older versions and legacy properties. Removing a seemingly unused property can break existing maps.
- Changing an asset on disk does not guarantee that `AssetManager` or the editor preview reloads it; use the reimport/reload services and invalidate caches as needed.

## Known game/editor controls

Gameplay documented in `README.md`: WASD moves, the mouse looks around, Space/Ctrl move up/down in noclip, Shift sprints, F toggles the flashlight, E interacts, left mouse fires/throws, R reloads, 1/2 select weapons, 0 holsters the weapon, Esc pauses/toggles the cursor, and the wheel changes FOV.

Development shortcuts: backtick opens the console, Insert toggles ImGui, F1 noclip, F2 screenshot, F3 spider pursuit, F4 reload shaders, F5 reload map, F6 patrol AI, F8 enemy selection, F9 debug drawer, F10 post-processing, and F12 shadows. G/J/V/T/Z perform the test actions documented in the README.

## Complete C# file inventory

The tables below were checked against `rg --files -g '*.cs'`. Each C# file appears once.

### `Blowtorch` project

| File | Purpose |
|---|---|
| `Blowtorch/AssetBrowserSupport.cs` | Asset types, metadata, and drag-and-drop payloads used by the Asset Browser. |
| `Blowtorch/AssetBrowserWindow.cs` | Asset Browser window: search, filters, tiles, details, selection, delete, reimport, and activation. |
| `Blowtorch/BlowtorchSettings.cs` | Persisted editor settings, including paths and startup options. |
| `Blowtorch/BrushPreviewManager.cs` | Creates and updates the temporary mesh/entity used for brush preview. |
| `Blowtorch/CommandHistory.cs` | Command and history contracts, including scene and terrain snapshots for undo/redo. |
| `Blowtorch/EditorApplication.cs` | Main editor lifecycle, GL window, ImGui, services, rendering, and Play/F5. |
| `Blowtorch/EditorAssetService.cs` | Asset catalog and operations: locate, import, load, reload, remove, revision tracking, and cached procedural staircase meshes. |
| `Blowtorch/EditorGizmo.cs` | Math, hit testing, and drawing for editor transform axes/gizmos. |
| `Blowtorch/EditorInputService.cs` | Input contexts, UI/viewport capture, and editor event routing. |
| `Blowtorch/EditorLightingSystem.cs` | Editor light visualization and shadow-resource/cache management. |
| `Blowtorch/EditorSceneService.cs` | Owner of the current map document, the synchronized map scene, and the isolated procedural-generator preview scene; loading, saving, synchronization, procedural staircase regeneration, terrain/neighbors, and forced LOD 0. |
| `Blowtorch/EditorUI.cs` | Main ImGui UI: menus, hierarchy, terrain creation/sculpt/preview, neighbors, skybox, clouds, fog, ocean, materials, transforms, group collision selection, and common-field multi-selection editing. |
| `Blowtorch/EditorViewport.cs` | Editor viewport/framebuffer, cameras, picking, gizmos, and 3D/orthographic view rendering. |
| `Blowtorch/EditorWindow.cs` | GLFW/OpenGL editor-window wrapper and window/input callbacks. |
| `Blowtorch/GeometryGraphEditorWindow.cs` | Visual node editor for `GeometryGraph`/`.fgeo` assets. |
| `Blowtorch/MaterialEditorWindow.cs` | Visual material editor: load/save/reload, thumbnails, nodes, and inspector. |
| `Blowtorch/MaterialGraphValidator.cs` | Diagnostics and validation for material graph types, sockets, and compatibility. |
| `Blowtorch/MaterialPreviewRenderer.cs` | Offscreen material preview renderer for spheres, cubes, or planes. |
| `Blowtorch/Program.cs` | Editor entry point; creates and runs `EditorApplication`. |
| `Blowtorch/TerrainHeightmapBrush.cs` | Heightmap loading/sampling, including grayscale EXR, and pixels for preview/sculpt. |
| `Blowtorch/UndoManager.cs` | Integrates undo/redo commands and snapshots with `EditorSceneService` and assets. |
| `Blowtorch/ViewportCamera.cs` | Free-fly/orbit/orthographic viewport camera and navigation operations. |

### `Fuse` project — entry point and animation

| File | Purpose |
|---|---|
| `Fuse/Program.cs` | Game entry point; selects the initial map and calls `Application.Init/Run`. |
| `Fuse/src/Animation/AnimationClip.cs` | Clip/channel data imported from Assimp animations. |
| `Fuse/src/Animation/Animator.cs` | Evaluates clips and time and produces final bone/node matrices. |
| `Fuse/src/Animation/Bone.cs` | Skinning bone metadata, index, and offset. |
| `Fuse/src/Animation/ProceduralSpiderWalk.cs` | Procedural multi-leg spider gait/IK and contact events. |
| `Fuse/src/Animation/Skeleton.cs` | Bone/node hierarchy and local/global matrix calculation. |
| `Fuse/src/Animation/SkinnedModel.cs` | GPU skinned-model asset, submeshes, and resource disposal. |

### `Fuse` project — assets, audio, and behaviours

| File | Purpose |
|---|---|
| `Fuse/src/AssetManagement/AssetManager.cs` | Central cache/loader for models, textures, materials, meshes, shaders, and skinned assets, with an upload queue. |
| `Fuse/src/Audio/AudioSystem.cs` | SoLoud wrapper for sounds, music, preload, 3D audio, listener, and pause. |
| `Fuse/src/Audio/ImpactSoundSystem.cs` | Watches physics contacts and triggers impact sounds. |
| `Fuse/src/Behaviours/BehaviourAttribute.cs` | Attribute associating metadata/name with behaviour types. |
| `Fuse/src/Behaviours/BehaviourData.cs` | Serializable representation of a behaviour type and JSON properties. |
| `Fuse/src/Behaviours/BehaviourSystem.cs` | Registry, reflection-based creation, updating, and lifecycle of behaviours. |
| `Fuse/src/Behaviours/ExportAttribute.cs` | Marks behaviour fields/properties for editor exposure/serialization. |
| `Fuse/src/Behaviours/IBehaviour.cs` | Lifecycle interface for behaviours attached to entities. |
| `Fuse/src/Behaviours/MovingFloor.cs` | Behaviour for a kinematic platform/floor moving between points. |
| `Fuse/src/Behaviours/TriggerReset.cs` | Behaviour requesting a scene reset when entering a trigger. |
| `Fuse/src/Behaviours/TriggerSystem.cs` | Detects trigger overlaps and calls the corresponding behaviours/reset. |

### `Fuse` project — core and debug

| File | Purpose |
|---|---|
| `Fuse/src/Core/Application.cs` | Runtime orchestrator: initialization, loop, pause, fixed physics, scene, renderer, input, audio, and HUD. |
| `Fuse/src/Core/Bible.cs` | Asset path catalog and preload helpers for shaders, textures, models, audio, and fonts. |
| `Fuse/src/Core/DevShortcuts.cs` | Development shortcuts: reloads, debug drawer, post-processing, shadows, maps, spawning, and tests. |
| `Fuse/src/Core/Engine.cs` | Engine clock/FPS/delta and controlled advancement of simulation time. |
| `Fuse/src/Core/EngineProfiler.cs` | Per-frame CPU section timing, history, and performance counters. |
| `Fuse/src/Core/GameNotify.cs` | Transient notifications drawn on the game screen. |
| `Fuse/src/Core/Logger.cs` | Console/memory logging, warnings, errors, and diagnostic popups. |
| `Fuse/src/Core/MathUtil.cs` | High-level math helpers such as degrees and MoveTowards. |
| `Fuse/src/Core/ResPath.cs` | Resolves the `res` root from the executable, ancestors, and known directories. |
| `Fuse/src/Core/ScreenshotService.cs` | OpenGL readback, vertical flip, and PNG screenshot saving. |
| `Fuse/src/Core/Window.cs` | GLFW window/OpenGL 4.3 context with callbacks, resize, cursor, and input initialization. |
| `Fuse/src/Debug/DebugDrawer.cs` | Renderer for lines, gizmos, billboards, icons, and debug visuals. |
| `Fuse/src/Debug/IGizmoDrawable.cs` | Contract for objects that draw a debug/gizmo representation. |

### `Fuse` project — enemies

| File | Purpose |
|---|---|
| `Fuse/src/Enemy/Enemy.cs` | Simple/generic enemy implementation with interface and debug drawing. |
| `Fuse/src/Enemy/EnemyPatrol.cs` | Generic patrol state machine. |
| `Fuse/src/Enemy/EnemySystem.cs` | Owns enemy/spider lists, spawning, updates, cleanup, and physics integration. |
| `Fuse/src/Enemy/IEnemy.cs` | Enemy lifecycle, update, rendering, and selection contract. |
| `Fuse/src/Enemy/SpiderDamageBody.cs` | Creates collision/damage bodies for spider parts. |
| `Fuse/src/Enemy/SpiderDeathBody.cs` | Spider death/ragdoll physical bodies, pose, and velocity. |
| `Fuse/src/Enemy/SpiderEnemy.cs` | Main spider enemy: AI, locomotion, patrol, surface movement, procedural walk, ragdoll, and debug. |
| `Fuse/src/Enemy/SpiderLocomotionProfile.cs` | Configurable spider movement, gait, and physics parameters. |
| `Fuse/src/Enemy/SpiderPatrol.cs` | Spider-specific patrol state machine. |
| `Fuse/src/Enemy/SpiderRagdollDefinition.cs` | Serializable definition of ragdoll parts, shapes, and joints. |
| `Fuse/src/Enemy/SpiderSurfaceMotor.cs` | Spider movement/restriction on arbitrary surfaces with physics contacts. |
| `Fuse/src/Enemy/SpiderSurfacePursuitPlanner.cs` | Route/detour planner for pursuit across surfaces. |
| `Fuse/src/Enemy/SpiderSurfaceSolver.cs` | Surface contact probes, tangent basis, projection, and debug. |
| `Fuse/src/Enemy/SpiderTargetSurfaceResolver.cs` | Selects/reconstructs the target surface for spider navigation. |

### `Fuse` project — ImGui and input

| File | Purpose |
|---|---|
| `Fuse/src/Imgui/Console.cs` | In-game console, commands, log text, and map/sky commands. |
| `Fuse/src/Imgui/ImGuiBackEnd.cs` | ImGui.NET/OpenGL backend: buffers, fonts, input, and drawing. |
| `Fuse/src/Imgui/OrientationGizmo.cs` | Visual orientation/axis gizmo inside ImGui. |
| `Fuse/src/Input/Input.cs` | GLFW polling, current/edge states, mouse, wheel, and cursor. |
| `Fuse/src/Input/InputContext.cs` | Input contexts and priorities between debug, UI, weapon, noclip, and gameplay. |
| `Fuse/src/Input/KeyCodes.cs` | Numeric constants compatible with GLFW keys. |

### `Fuse` project — interaction

| File | Purpose |
|---|---|
| `Fuse/src/Interaction/ButtonInteract.cs` | Concrete interactable for buttons. |
| `Fuse/src/Interaction/CubeInteract.cs` | Concrete interactable for cubes/test objects. |
| `Fuse/src/Interaction/DoorInteract.cs` | Concrete interactable for doors. |
| `Fuse/src/Interaction/Interactable.cs` | Base interface for objects that the player can activate. |
| `Fuse/src/Interaction/InteractableTypeAttribute.cs` | Attribute mapping a serialized name to an interactable type. |
| `Fuse/src/Interaction/InteractionSystem.cs` | Registry/reflection and creation of interactables by type. |
| `Fuse/src/Interaction/PlayerInteraction.cs` | Camera raycast, crosshair/prompt, and interaction execution. |

### `Fuse` project — math and physics

| File | Purpose |
|---|---|
| `Fuse/src/Math/AABB.cs` | Axis-aligned bounds and intersection/containment/raycast operations. |
| `Fuse/src/Math/MathUtils.cs` | Vector3, Quaternion, Matrix, and interpolation utilities. |
| `Fuse/src/Physics/DefaultFilters.cs` | Default Jolt collision filters. |
| `Fuse/src/Physics/EnemyBodyFilter.cs` | Filter for enemy-specific bodies. |
| `Fuse/src/Physics/Explosion.cs` | Radial impulse and damage for explosions. |
| `Fuse/src/Physics/OceanPhysicsSystem.cs` | Shared-ocean convex clipping, submerged volume/centroid, Archimedean buoyancy, central translation drag, inertia-stable rotational drag, deterministic hydrostatic validation, F9 diagnostics, and player submersion state. |
| `Fuse/src/Physics/PhysicsWorld.cs` | Jolt world/body-system initialization, stepping, queries, and debug. |
| `Fuse/src/Physics/RigidBody.cs` | Jolt body wrapper, primitive/mesh/compound shape builders, authored mass/inertia, force/torque access, true center-of-mass/inverse-inertia queries, collider volume, compound child definitions, and optional buoyancy-volume override. |

### `Fuse` project — player and weapons

| File | Purpose |
|---|---|
| `Fuse/src/Player/IWeapon.cs` | Weapon contract: update, rendering, firing, reload, and state. |
| `Fuse/src/Player/PickupController.cs` | Raycast for holding, moving, throwing, and updating physical objects. |
| `Fuse/src/Player/Player.cs` | Character controller, input, movement, water sinking/float control, camera, health, damage, spawn, and flashlight. |
| `Fuse/src/Player/WeaponSystem.cs` | Weapon registry, current weapon, equip/switch, update, rendering, and physics. |
| `Fuse/src/Player/Weapons/AKWeapon.cs` | AK implementation: model, animation, firing, reload, and audio. |
| `Fuse/src/Player/Weapons/GlockWeapon.cs` | Glock implementation: model, animation, firing, reload, and audio. |

### `Fuse` project — renderer and lighting

| File | Purpose |
|---|---|
| `Fuse/src/Renderer/Camera.cs` | Perspective camera, view/projection, rays, FOV, and rotation. |
| `Fuse/src/Renderer/ComputeShader.cs` | OpenGL compute-program wrapper and uniform/binding setters. |
| `Fuse/src/Renderer/DeathScreen.cs` | Death-screen overlay/post-process and reload. |
| `Fuse/src/Renderer/ForwardPlusLighting.cs` | Local-light tiled culling and SSBO/list storage. |
| `Fuse/src/Renderer/ImageBasedLighting.cs` | Environment cubemap, irradiance, and prefilter creation/use for IBL. |
| `Fuse/src/Renderer/Light.cs` | Directional, point, and spot light model, parameters, flags, and shadows. |
| `Fuse/src/Renderer/LightingBuffer.cs` | Lighting UBO: directional light, local arrays, and shadow data. |
| `Fuse/src/Renderer/MasterRenderer.cs` | Orchestrates every pass: shadows, sky, objects, ocean, clouds, fog, underwater, and post-process. |
| `Fuse/src/Renderer/Mesh.cs` | Mesh/VAO/VBO/IBO, parts, draw operations, bounds, and GPU ownership. |
| `Fuse/src/Renderer/ModelLoader.cs` | Assimp static-model import, meshes, materials, hulls, and collision vertices. |
| `Fuse/src/Renderer/OceanRenderer.cs` | Visual ocean with spectral/FFT cascades, adaptive mesh, normal map, and underwater. |
| `Fuse/src/Renderer/OceanSurfaceSampler.cs` | CPU H0 spectrum, shared ocean displacement/normal/velocity sampling, and cascade helpers. |
| `Fuse/src/Renderer/PointShadowMap.cs` | Point-light shadow framebuffer/cubemap. |
| `Fuse/src/Renderer/ProceduralSky.cs` | Procedural-sky parameters, functions, ambient colors, and IBL support. |
| `Fuse/src/Renderer/ProceduralGrassRenderer.cs` | GPU-driven grass: candidate/patched SSBOs, compute culling, three indirect LOD draws, near shadows, weighted species tinting, optional Hi-Z patch occlusion, and F9 diagnostics. |
| `Fuse/src/Renderer/Scene.cs` | Transform, Entity, and Scene; hierarchy, rendering, shadows, terrain LOD, entities, and lights. |
| `Fuse/src/Renderer/Shader.cs` | Shader compilation/linking, `#include`, uniforms, and uniform blocks. |
| `Fuse/src/Renderer/ShadowMap.cs` | 2D/layered directional and spot shadow resource. |
| `Fuse/src/Renderer/SkinnedMesh.cs` | GPU mesh with skinned vertices and data. |
| `Fuse/src/Renderer/SkinnedModelLoader.cs` | Assimp import of skinned models, skeletons, animations, materials, and meshes. |
| `Fuse/src/Renderer/Texture.cs` | Image decode/upload, color space, mipmaps, binding, cache, and dominant color. |
| `Fuse/src/Renderer/TextureArray.cs` | OpenGL `Texture2DArray` upload from terrain/material layer images, filtering, fallback layers, binding, and disposal. |
| `Fuse/src/Renderer/UIRenderer.cs` | 2D batching of images, solids, and text for UI. |
| `Fuse/src/Renderer/ViewFrustum.cs` | Frustum culling and sphere testing. |
| `Fuse/src/Renderer/VolumetricCloudRenderer.cs` | Cloud noise/weather/LUT generation, raymarching, lighting, shadows, temporal history, and composition. |
| `Fuse/src/Renderer/VolumetricFogRenderer.cs` | Low-resolution fullscreen fog, noise, shafts, lights, shadows, temporal history, and composition. |

### `Fuse` project — post-processing and materials

| File | Purpose |
|---|---|
| `Fuse/src/Renderer/PostProcess/FramebufferPool.cs` | Reusable framebuffer and texture pool for post-processing. |
| `Fuse/src/Renderer/PostProcess/FullscreenQuad.cs` | Fullscreen geometry used by screen-space passes. |
| `Fuse/src/Renderer/PostProcess/PostProcessPipeline.cs` | HDR FBO, composite chain, tonemapping, bloom, SSAO, and motion blur. |
| `Fuse/src/Renderer/PostProcess/PostProcessSettings.cs` | Post-processing toggles and parameters. |
| `Fuse/src/Renderer/PostProcess/PostProcessShader.cs` | Post-process shader wrapper and GLSL includes. |
| `Fuse/src/Renderer/Materials/MaterialAsset.cs` | JSON material schema, alpha, two-sided, shadows, graph properties, vector2 values, layer-path arrays, and exposed parameters. |
| `Fuse/src/Renderer/Materials/MaterialGraphCompiler.cs` | Compiles material graphs into GLSL, world-space terrain/noise/triplanar/layer expressions, ordinary texture slots, and texture-array slots. |
| `Fuse/src/Renderer/Materials/MaterialNodeCatalog.cs` | Material node catalog, socket types, defaults, terrain nodes, procedural nodes, triplanar nodes, and layer-blend nodes. |
| `Fuse/src/Renderer/Materials/MaterialRuntime.cs` | GPU-loaded material, uniform binding, ordinary texture binding, texture-array binding, shader ownership, and disposal. |

### `Fuse` project — scene and map model

| File | Purpose |
|---|---|
| `Fuse/src/Scene/Geometry/GeometryGraph.cs` | CPU geometry nodes/evaluator, cache, and `MeshData/material` output. |
| `Fuse/src/Scene/MapSerializer.cs` | `.bth` serialization/deserialization for scenes, objects, settings, bodies, lights, procedural staircases, and deferred hierarchy compound-body construction. |
| `Fuse/src/Scene/Model/Brush.cs` | CSG or editable-mesh brush, faces, transform, body, and bounds. |
| `Fuse/src/Scene/Model/CSGOperations.cs` | Convex/plane-based CSG operations. |
| `Fuse/src/Scene/Model/EditableBrushMesh.cs` | Vertex/edge/face topology, editing, triangulation, normals, and bounds. |
| `Fuse/src/Scene/Model/Face.cs` | Face plane, material/texture slot, and UV axes/scale/offset/rotation. |
| `Fuse/src/Scene/Model/MapBody.cs` | Serializable primitive/compound shape, physical properties, and optional ocean buoyancy volume of a map object. |
| `Fuse/src/Scene/Model/MapDocument.cs` | Editor/map JSON DOM, settings, objects, spawn, parsing, and validation. |
| `Fuse/src/Scene/Model/MapObject.cs` | Serialized object base: parent, mesh/model/terrain, material, visibility, body, optional procedural staircase, lights, and behaviours. |
| `Fuse/src/Scene/Model/MapPlayerSpawn.cs` | Player spawn position, yaw, and pitch. |
| `Fuse/src/Scene/Model/MeshGenerator.cs` | CPU mesh generation for brushes and faces. |
| `Fuse/src/Scene/Model/StaircaseMeshGenerator.cs` | Generates the bounds-preserving staircase surface and matching compact box-per-step collision layout. |
| `Fuse/src/Scene/Model/StaircaseSettings.cs` | Serializable staircase parameters, defaults, validation, and JSON conversion. |
| `Fuse/src/Scene/Model/OceanSettings.cs` | Persisted ocean settings: level/size/grid, waves, surface, physics, player water behavior, normal map, and underwater. |
| `Fuse/src/Scene/Model/SceneNameManager.cs` | Unique IDs, name repair, and scene-hierarchy validation. |
| `Fuse/src/Scene/Model/SkyboxSettings.cs` | Persisted textural/procedural skybox settings, day/night, sun, stars, and atmosphere. |
| `Fuse/src/Scene/Model/VolumetricCloudSettings.cs` | Cloud settings/presets, density, shape, erosion, wind, quality, temporal behavior, and shadows. |
| `Fuse/src/Scene/Model/VolumetricFogSettings.cs` | Persisted fog settings: height, density, noise, wind, shafts, steps, resolution, and temporal behavior. |
| `Fuse/src/Scene/SceneManager.cs` | Active scene, path, bodies, load/reload, compound-collider debug drawing, behaviour/animator updates, raycasts, and triggers. |

### `Fuse` project — terrain

| File | Purpose |
|---|---|
| `Fuse/src/Scene/Terrain/ProceduralTerrainAsset.cs` | Binary `.terrain` v3 procedural recipe and sparse sample overrides/deltas. |
| `Fuse/src/Scene/Terrain/ProceduralTerrainGenerator.cs` | Deterministic double-domain macro/mountain/valley/detail/domain-warp terrain generation and normal sampling. |
| `Fuse/src/Scene/Terrain/ProceduralTerrainLayer.cs` | Runtime scene-layer descriptor connecting a procedural asset to streaming, transforms, materials, and collision budgets. |
| `Fuse/src/Scene/Terrain/ProceduralTerrainSettings.cs` | Serialized generator parameters, bounded preview/streaming/LOD budgets, and the nested grass recipe version. |
| `Fuse/src/Scene/Terrain/ProceduralGrassSettings.cs` | Persisted grass profile, species palette, placement rules, wind/lighting settings, sparse-mask reference, and optional Hi-Z settings. |
| `Fuse/src/Scene/Terrain/ProceduralGrassPatchSet.cs` | Asynchronous deterministic grass-patch generation tied to resident terrain tiles, including biome/slope/water/mask filters, clumps, and species selection. |
| `Fuse/src/Scene/Terrain/ProceduralGrassDensityMaskStore.cs` | Thread-safe sparse tiled R8 mask cache, bilinear sampling, painting/erasing, and atomic sidecar persistence. |
| `Fuse/src/Scene/Terrain/TerrainAsset.cs` | Binary `.terrain` v1 tile, `ushort` samples, bilinear and renderer-matching triangulated surface queries, raycast, sculpt, and brush tools. |
| `Fuse/src/Scene/Terrain/TerrainLodSet.cs` | GPU meshes per LOD level, geometric errors, current level, stitching, and disposal. |
| `Fuse/src/Scene/Terrain/TerrainMeshGenerator.cs` | Terrain grid/chunk generation, normals, UVs, up to five LODs, and edge stitching. |
| `Fuse/src/Scene/Terrain/TerrainSceneBuilder.cs` | Creates chunks/LODs and Jolt collision, handles tile neighbors, and refreshes geometry. |
| `Fuse/src/Scene/Terrain/TerrainSculptTool.cs` | Enum for height/noise sculpting plus PaintGrass and EraseGrass mask tools. |
| `Fuse/src/Scene/Terrain/TerrainQuadTree.cs` | CPU screen-error quadtree patch selection for future global terrain/horizon LOD. |
| `Fuse/src/Scene/Terrain/TerrainStreamer.cs` | Asynchronous procedural tile generation, cancellation, residency, and ready/eviction queues. |
| `Fuse/src/Scene/Terrain/TerrainTileSetAsset.cs` | v2 `.terrain` tile set with neighbors in one file, v1 compatibility, and v3 procedural preview materialization. |

### `Fuse` project — UI

| File | Purpose |
|---|---|
| `Fuse/src/UI/EnemyDebugHUD.cs` | Enemy debug list, selection, and overlay. |
| `Fuse/src/UI/FontAtlas.cs` | Bitmap atlas generation from TTF and glyph metadata. |
| `Fuse/src/UI/GameplayHUD.cs` | Crosshair, weapon, status, notifications, and gameplay elements. |
| `Fuse/src/UI/HUD.cs` | HUD element collection, layout, and drawing. |
| `Fuse/src/UI/HUDElement.cs` | Base HUD element layout and anchoring contract. |
| `Fuse/src/UI/HUDImage.cs` | HUD image/texture element. |
| `Fuse/src/UI/HUDPanel.cs` | HUD panel/rectangle element. |
| `Fuse/src/UI/HUDText.cs` | HUD text element. |
| `Fuse/src/UI/LoadingScreen.cs` | Loading UI and progress display. |

## Checklist before a change

- [ ] I read this documentation, `README.md`, and `PLAN.md` if it exists.
- [ ] I checked `git status` and will not overwrite the user’s work.
- [ ] I identified whether the source of truth is `MapDocument`, `Renderer.Scene`, an asset, or a shader.
- [ ] I checked whether the change must be applied in both editor and runtime.
- [ ] I kept simulation time, pause behavior, and the GL thread/context consistent.
- [ ] I preserved serialization compatibility and defaults for old maps.
- [ ] I invalidated caches/history when changing a format, resolution, camera, or shader.
- [ ] I built `FuseEngine.slnx` and performed the appropriate visual validation in the editor/game.
- [ ] I updated this document if the architecture, contracts, formats, or inventory changed.
