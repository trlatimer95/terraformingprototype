# P0 — grid terraforming spike

Smallest honest test of the terrain data model: a fixed-point vertex heightfield, one
edit command, a generated mesh, and a collider that matches. Everything else is
deliberately absent.

## Required built-in modules

`Packages/manifest.json` must declare `com.unity.modules.terrain` and
`com.unity.modules.terrainphysics` for the Unity Terrain comparison view. Without them
`Terrain`, `TerrainData`, and `TerrainCollider` fail to resolve — the type-forwarder
error names the module to enable.

## Running it

1. Unity Hub → **Add** → `D:\Dev\GameDev\Tests\Terraform` (Unity 6000.3.9f1).
2. First open takes a minute while Unity generates `Library/`.
3. Press **Play** in whatever scene opens — `P0Bootstrap` auto-spawns and builds the
   scene in code, so there is nothing to wire up.
4. Optional, once you want a scene to keep: **Tools ▸ Terraform ▸ Create P0 Scene**.

## Two models

Press **M** to swap between them at runtime. Both are built at startup and occupy the same
space, with only one visible and collidable at a time, so each keeps whatever you sculpted
in it — you can flip back and forth comparing the same ground shaped two ways.
`P0Bootstrap.StartWithCellModel` picks which one you land in.

**Cell model (default).** One height per cell, stored at its **centre**, plus a flattened
flag. Heights are fixed-point at 1/20 m, so every tenth of a metre is exact and the
readouts stay clean decimals — the divisor need not be a power of two, since determinism
comes from storing integers at all. A `ushort` still reaches 3276 m of world height. Corner heights are *derived*: the **median** of the cells meeting there.

Median rather than mean, and that choice does real work. A mean lets one raised cell drag
its own corners upward, so a pile floats above the surrounding ground and tilts all four
neighbours into a star. The median ignores a lone outlier entirely — pile one cell as high
as you like and its corners stay exactly where they were — while four cells raised
together do move it, so broad ground still meshes. It is an edge-preserving filter.

Corners and edge midpoints are both derived, and which one carries a ridge depends on the
arrangement. Two **edge-adjacent** raised cells meet at their shared edge's midpoint, so
the crest runs centre to centre and the corners stay low — lifting them would level the
edge end to end. Two **diagonal** cells share no edge, so there the corner is the only
thing that can carry the crest, and each side takes its own height with the step between
becoming the ridge face.

A flattened cell overrides the shared corner, raising it to meet the pad so neighbours
**slope up** to it rather than being cut off by a wall.

Two flattened cells at different heights are the one case that produces a real vertical
face, and **flatten now dissolves it**: levelling a cell releases the flattened flag on any
of its **eight** neighbours sitting at a different height. That neighbour goes back to
blending, its edge drops to meet the new pad, and the join becomes a slope with no face at
all. A face there is a strip of ground texture stretched over zero width, which is the
worst thing terracing does.

Diagonals are included deliberately. Two pads meeting only at a corner still raise that
corner to the flattened height, so the face lands on the ordinary cells *between* them
rather than between the pads — the same stretched strip, just displaced a cell.

The newer pad wins, and neighbours at the *same* height keep their flags — so building a
pad cell by cell still ends with one rigid pad rather than dissolving as it grows.

Measured by replicating the mesh builder's own face test over the whole grid:

| Two pads at 5.0 m and 4.0 m | orthogonal release | full ring |
| --- | --- | --- |
| Side by side | 0.00 m | 0.00 m |
| Corner to corner | 1.00 m | 0.00 m |
| Not touching | 0.00 m | 0.00 m |

The released pad's edge drops to 4.00 m while its centre stays at 5.00 m, so it slopes
rather than flattening down.

Note that this model cannot be rendered by Unity Terrain — a heightmap has one value per
vertex and no per-cell centre, which is exactly the thing that makes a cell able to hold a
peak. The `T` comparison and terrain holes exist only in the vertex model.

The stored centre is what the vertex model could never have: there, a quad's interior is
interpolated from its corners, so a cell cannot hold a peak of its own. Together with the
median rule that gives:

- Raising a cell builds a pyramid **confined to that cell**, with its base at the
  surrounding ground level. Piling higher makes it taller, not wider.
- Flattening brings the cell's four edges to its centre — a genuinely flat face — and the
  neighbours slope up to meet it.
- Two flattened cells at the same height mesh seamlessly; at different heights the older
  one is released and they meet in a slope.
- Untouched ground stays organic. Only deliberately levelled ground goes crisp.

Walls appear only where two cells genuinely disagree, which now means terrace edges rather
than every pad boundary.

**Vertex model.** Heights at vertices. Sculpt raises a **corner**; flatten levels the
**cells between** corners, so building a pad means raising the corners around it and then
levelling the area they enclose. Flatten targets the **mean of the cell's four corners**,
snapped to the current step — cut on the high side, fill on the low side, balancing to no
net change in material. Targeting the lowest corner instead would only ever dig, which
reads as the tool removing ground you did not ask it to.

Neither model holds a **datum**. Both used to, captured on a right-click, and in both it
outlived whatever the player did next: sculpt a cell, level it, and it snapped back to a
height chosen minutes earlier while the HUD showed a number that never moved because it had
nothing to do with the cell under the crosshair. Flatten now reads the targeted cell every
time it runs. Arguably the more realistic workflow — you shape the
boundary, then the ground inside it. Also the only model that can drive Unity Terrain, so
`T` and terrain holes live here.

## Controls — cell model

| Input | Action |
|---|---|
| `1` / `2` | tool — sculpt / flatten |
| `LMB` / `RMB` | raise / lower the targeted cell (sculpt), or level it (flatten) |
| Scroll | step — 5 cm / 10 cm / **25 cm** / 50 cm / 1 m |
| `-` / `=` | max step — 1 m / 2 m / **3 m** / 4 m / unlimited |
| `WASD`, `Space` | walk, jump |
| `Z` / `Y` | undo / redo |
| `M` | swap terrain model |
| `Tab` | toggle cell outlines |
| `R` | reset to flat |
| `Esc` | release the cursor |

Every nearby cell shows its **height as a number**. Flattened cells are
labelled amber, natural ones grey. Being able to read the exact height before touching it
is most of what separates precise terraforming from fiddly terraforming.

**Sculpt** — `LMB` raises the cell's centre, `RMB` lowers it. Sculpting **clears the
flattened flag**: a cell you just dug is no longer level, so it goes back to meshing with
its neighbours until you flatten it again.

**Flatten** — `LMB` pins the cell's corners to its centre, and releases any of the eight
neighbours that was flattened at a *different* height so the two slope together instead of
stepping. The height is always the cell's **own, read at the moment you click**.

**Max step** caps how far a cell may stand above an orthogonal neighbour. Default **3 m**,
which is roughly where a side wall gets taller than any texture can cover and stops
reading as terrain. The HUD says *blocked* when an op is refused.

## The surface comparison

`T` swaps which representation renders and collides. **Both are driven by the same
`HeightGrid` and the same command log** — identical data, identical edits, two renderers.
Both update on every edit so their costs can be read side by side in the HUD; only one is
visible and solid at a time.

What to actually compare:

- **Edge crispness.** Raise one vertex 1 m and look along the step. Unity Terrain applies
  geometric LOD driven by `heightmapPixelError` (`[` / `]`, default 5). Drop it to 0 and
  Terrain matches the mesh closely; raise it and watch sharp steps soften with distance.
  Whether that softening is acceptable is the decision.
- **Triangulation.** The mesh view picks each cell's diagonal along the flatter axis;
  Terrain uses a fixed pattern. On non-planar quads the two surfaces genuinely differ.
- **Edit cost.** Mesh rebuild + collider re-cook versus `SetHeights`. At 33×33 both are
  trivial; the ratio is what extrapolates.
- **Precision.** Terrain stores normalised heights against `size.y` (`TerrainHeightRange`,
  64 m here), so its step is `heightRange/65535` ≈ 1 mm. At a 600 m range it would be
  ~9 mm — coarser than the mesh view's fixed 6.25 cm quantisation is fine, but the
  precision becomes a function of world height rather than a constant.

### Terrain holes

`H` punches a hole in the cell at the crosshair — this is the basis of the hybrid posture:
heightmap surface, hole at the shaft mouth, separate geometry taking over below.

Walk to a hole's edge and look in. The thing to notice is that a hole removes the cell
from both rendering and collision and **generates no side walls** — you get a clean gap
with nothing bounding it. Every shaft wall, floor, and ceiling under that hole is geometry
you generate yourself. That is the real scope of posture B, and it is worth feeling before
committing to it.

The `TerrainData` is created at runtime and never saved. Mutating a `TerrainData` that came
from a project asset writes through to disk and permanently alters the source world.

## Sky and light

The demo renders a procedural sky (`Skybox/Procedural`, no textures) and `L` cycles
three lighting presets: midday, low sun, overcast.

This is not decoration. Terrain relief is read almost entirely from shading, and a high
midday sun flattens exactly the small steps and cut edges the playtest asks people to
judge. A low sun at 13 degrees makes a 0.1 m terrace obvious; flat overcast light hides
it. Being able to swap tells you whether an edge reads because of its shape or only
because of where the sun happened to be.

Ambient light is taken from the sky (`AmbientMode.Skybox`), so each preset calls
`DynamicGI.UpdateEnvironment()`. Without it the world keeps the previous preset's
bounce light while the sky itself changes.

The sky material is generated into `Resources/P0/Sky.mat` by **Tools ▸ Terraform ▸
Prepare Build**, like the other three — see the note in `Assets/Editor/BuildPrep.cs`
for why nothing here relies on `Shader.Find` alone.

## Generated ground

By default the world is `DemoTerrain` — a flat sandbox ringed by a hill, rolling ground, a
small mountain and a basin. To work on generated ground instead, put a Unity Terrain in the
scene and set **Ground source** to `SceneTerrain` on the bootstrap.

It reads a `Terrain`, not any particular generator, so Gaia, the built-in terrain tools,
MapMagic and a plain heightmap import are all equally valid sources and none of them are a
dependency of this project.

The grid is 64 m across and a generated world is usually a kilometre or more, so the import
takes a **window** out of the source rather than the whole thing:

| Field | Meaning |
| --- | --- |
| `SampleOrigin` | World XZ the near corner of the window sits on |
| `SampleStride` | Source metres per grid metre. `1` is real scale |
| `SampleFloorMetres` | Where the window's low point lands, leaving room to dig below it |
| `HideSourceTerrain` | Take the source out of the scene once it has been read |

`N` slides the window to the next position and `Shift+N` back, in half-window steps,
re-importing **both** models so they stay the same piece of ground. Above a stride of 1 the
window covers more ground as a true-scale miniature — heights shrink to match — which is
for finding a spot worth looking at, not for judging how the tools feel.

Both models are rebased by a **single shared vertical offset**. If they rebased separately
they would stop being the same ground and the comparison would mean nothing.

### Generated ground is steeper than the guard

A generated mountainside routinely arrives with more than 3 m between neighbouring cells —
cliffs the player did not make. The max-step rule is therefore **not** a flat ceiling: an
edit is allowed if it lands inside the guard **or if it does not deepen a step that was
already too steep**.

Without that concession every edit on a steep slope fails and the tools look broken exactly
where they matter most. With it the high side always keeps a legal move, so a slope is
worked from the top down — which is how terracing works anyway.

The import logs the steepest step it found and warns when the window exceeds the guard.

### Surface texture

`ImportTextures` bakes the source's painted terrain layers into a single diffuse map and
puts it on both models. `TextureResolution` defaults to 1024, which over a 64 m window is
16 pixels per metre; the bake runs again on every window move, so raising it costs a longer
hitch on `N`.

One baked map on the standard material, not a splatting shader. A custom shader is one more
thing that has to be dragged into a player build via Always Included Shaders, and that is
exactly how the world turned magenta the first time.

Both mesh builders already write world-planar UVs in metres, so nothing about the meshes
changed — only a texture scale that maps the window onto 0..1 of the baked map.

Two things this loses against the source:

- **Tiling detail.** A layer tiling every 5 m is captured at bake resolution, not at its
  own. The ground reads as ground, but close up it is softer than the source.
- **Vertical faces.** Seam faces take the UV of their footprint, so a cut face is a smear
  of the colour at that spot rather than a projected cliff texture.

If the source has no painted layers the flat colour stays and the import says so.

## Layout

```
Assets/Scripts/Core/     engine-portable: no MonoBehaviour, no Mesh, no rendering
  HeightGrid.cs          fixed-point vertex heightfield + coordinate math
  TerrainCommands.cs     ITerrainCommand + AdjustVertexCommand
  CommandLog.cs          execute / undo / redo, cut-fill tally
Assets/Scripts/View/     Unity-side rendering and collision
  ChunkMeshBuilder.cs    heightfield → mesh
  ChunkView.cs           mesh + collider, coalesces edits to one rebuild per frame
  UnityTerrainView.cs    same heightfield via Terrain/TerrainData, plus holes
  GridOverlay.cs         cell wireframe
Assets/Scripts/Play/     input, camera, HUD, scene construction
  FirstPersonController.cs  CharacterController walking + ground-rise recovery
  TerraformTool.cs          crosshair aiming, reach, step size, raise/lower
  InputCompat.cs            works under either Unity input backend
  P0Bootstrap.cs            builds the whole scene in code
```

`Core/` has no Unity dependency beyond `Vector3`/`Mathf`. If the 64-player dedicated
server target pushes this to Unreal, `Core/` is the part that gets ported and `View/`
and `Play/` are the parts that get thrown away.

## Known limits (intentional)

- One chunk, no streaming, no chunk-border vertex sharing.
- Whole-chunk mesh and collider rebuild per edit. The collider re-cook is the expensive
  half — the HUD reports both timings separately so the split is visible before it
  matters at P4.
- No slope limit. The `Validate()` hook for angle-of-repose is stubbed with a comment
  at the exact line it belongs.
- Single vertex per op. Brushes, flatten, and flatten-with-slope are P1.
- Surface only. Spans and tunnelling are P2.

## Next

**P1** — brush ops (raise/lower/flatten/flatten-with-slope/smooth), dirt inventory,
angle-of-repose slumping, save/load of the op log.

Gate for P1 is a feel test, not a feature list: hand-terrace a hillside into buildable
platforms with a graded ramp between them, and judge whether it is satisfying.
