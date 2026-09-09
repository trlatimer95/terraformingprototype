# Decision record

Working notes for the terrain investigation. Updated as decisions land; nothing here is
final until a measurement backs it.

**Last updated:** 7 September 2026

---

## D1 — Groundworks stays as the interaction baseline

The terraforming in this prototype already covers what the smaller game needs. Nothing that
follows replaces it. New backends are compared *against* it, using the same tasks, the same
1 m interaction scale, and the same tester questions.

**Consequence:** any backend experiment that quietly changes the player-facing tool scale is
measuring something else. A 0.25 m storage resolution still gets a 1 m tool footprint —
sixteen columns under one selection, not sixteen clicks.

## D2 — Settle the 64+ architecture first, then return to the smaller game

The goal is to stop re-asking whether a system built for 8–10 players scales. Verify the
terrain approach has a credible path to 64+, then scope back deliberately.

Framed precisely: **eliminate expensive architectural dead ends before returning to the
smaller game.** Not: build a 64-player platform before the smaller game can start.

**Consequence:** the testbed stops when it answers its question. It does not acquire
production polish. Keep it on separate branches/scenes from the playable game.

**Known tension:** a scope discrepancy surfaced during this discussion — the game spec
describes 8–10 player listen-server play while the prototype plans describe 64+ dedicated.
D2 is the resolution: investigate at 64+, implement at whatever game one needs.

## D3 — Leaning custom runtime backend, not committed

Both advisory passes independently landed on the same representation: a sparse column of
vertical material spans, immutable base plus explicit edited-column replacements, air as
absence, provenance surviving edits.

**Not yet committed.** The appearance gate can still kill it — the exact span mesher
produces stepped walls, and "smooth or irregular cave surfaces are preferred" is a stated
requirement.

**Honest reason to prefer it:** architectural control over concurrency, persistence format
and material identity. That is a legitimate reason. It is **not** evidence of better
performance, and it should not be presented as such until measured.

## D4 — Astra's Digger benchmark becomes a backend-neutral acceptance spec

The workload matrix, latency targets, join tests and soak criteria survive a backend change.
Only its Gate A is package-specific, and even there conservation, tool restrictions and
headless correctness are not.

**Consequence:** it is a set of acceptance criteria to point at whatever gets built, not a
programme that must be executed against Digger first.

## D5 — Two Digger tracks, both small

See `digger-spike.md`. Track B runs on a branch of this prototype; the vanilla track is a
separate throwaway project. Neither blocks the main sequence.

---

## What changed in the code during this discussion

### Two cut/fill accounting bugs, found by audit and fixed

Astra asked for the cut/fill counter to be inspected. It was wrong in both models — verified
against a brute-force whole-grid recount over 4,000 randomised grids.

| | Worst error before | After |
| --- | --- | --- |
| Cell model, sculpt | 1.025 m³ | 0.000000000 |
| Vertex model, sculpt | 2.033 m³ | 0.000000000 |

**Cell model.** The accounting region was 3×3. `SharedCornerRaw`'s diagonal tie-break
consults `NeighbourhoodMeanRaw` over a 4×4 block, so one edit can flip a corner *two* cells
away. Radius is now 2; radius 3 buys nothing.

**Vertex model.** It summed each vertex's own area × its height change. That looks correct
and is not: `ChunkMeshBuilder` splits each quad along its flatter diagonal, and the two
choices enclose different volumes on a non-planar quad. A per-vertex area sum returns
exactly the *average* of the two, so it reported a surface that is never built. It now
recounts the touched cells using the mesh's own diagonal rule.

Harnesses in `Tools/verification/`. They are Python replicas and can drift from the C# —
the proper home is EditMode tests against the real types.

### Other changes

- Flatten in **both** models now reads the targeted cell live. The held datum is gone from
  both; it was captured on a right-click and then silently outlived whatever the player did
  next.
- Vertex flatten targets the **mean of the four corners**, snapped to the current step. Cut
  equals fill; the snap prevents a levelled cell landing between steps where no number of
  clicks can meet it.
- Flatten releases mismatched flattened neighbours across the full eight-neighbour ring, so
  two pads at different heights meet in a slope rather than a stretched vertical face.
- Generated heights snap to 0.1 m so every generated cell is reachable by the tools.
- Heights now display in vertex mode, on the grid corners rather than cell middles.

---

## Step 1 results — measured 8 September 2026

Full-map rebuild on the development machine, main-thread synchronous. `LastMeshMs` brackets
the mesh builder alone; `LastColliderMs` brackets `sharedMesh = null` followed by
reassignment, which forces Unity to re-cook synchronously — so it is a real bake, and the
worst case. A production server would bake off-thread via `Physics.BakeMesh`.

| | Build | Cook | Triangles | µs/tri build | µs/tri cook | build : cook |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Vertex, Unity Terrain active | 1.09 ms | 1.39 ms | 8,192 | 0.133 | 0.170 | 0.78 |
| Vertex, generated mesh | 2.13 ms | 1.39 ms | 8,192 | 0.260 | 0.170 | 1.53 |
| Cell | 19.17 ms | 8.99 ms | 32,768 | 0.585 | 0.274 | **2.13** |

Triangle counts confirm both meshers exactly: 4,096 cells × 2 for the vertex model, × 8 for
the cell fan. **The cell figure currently carries zero seam faces** — nothing was flattened
when this was read — so 32,768 is its floor. Terraced ground adds four triangles per seam
quad and will push it up.

### The finding

**Mesh generation costs more than collision cooking — 2.13× in the cell model.** That
contradicts the "collision is the entire budget" hypothesis, which was never measured and is
now measured wrong. Cooking is the cheaper half in every configuration here.

Two consequences:

- The server cannot skip this cost by not rendering. It still needs collision *geometry*,
  and generating that geometry is the expensive part. A collision-only build could at least
  skip normals and UVs, which the current builders compute per triangle.
- Optimisation effort belongs in the mesher before the collider. The cell fan is 2.2× slower
  per triangle than the vertex mesher, which fits — each cell does eight corner and edge
  lookups, and each corner runs the widest-gap sort.

Both numbers are **full-map rebuilds**, which no production design would do. With 4 m bricks
and dirty-region rebuild the real figure is a small fraction of these. That is the "wrong
shape" cost from the earlier scaling analysis, now with a number on it.

### Projected onto a span brick

Using the measured rates, and assuming an exposed-face mesher emits 4–8 triangles per column
on open sloped ground (top cap plus one or two exposed sides; no bottom caps until tunnels):

| Brick | Columns | Triangles | Build + cook |
| --- | ---: | ---: | ---: |
| 4 m at 0.25 m | 256 | 1,024 – 2,048 | 0.9 – 1.8 ms |
| 4 m at 0.125 m | 1,024 | 4,096 – 8,192 | 3.5 – 7.0 ms |

At W3's 32 strokes/s touching roughly two bricks each, that is ~60–120 ms/s at 0.25 m and
~220–450 ms/s at 0.125 m, main-thread synchronous. Tight at the finer resolution, not fatal,
and both halves have headroom — off-thread baking for the cook, and a span mesher that should
be simpler than the eight-triangle fan for the build.

**Collision is not the wall.** The triangles-per-column assumption is the weak link in this
projection, and the appearance gate will replace it with a real count.

---

## Step 2 results — the appearance gate, 8 September 2026

Built `Assets/Scripts/Span/` and the `SpanGate` scene: a span world filled from the same
`DemoTerrain` height function the main prototype uses, so the comparison is between
representations rather than between terrains. Exact and smoothed meshers, 1 m to 0.125 m
columns, an axis-aligned mine, and interactive mining.

### The verdict

| | Result |
| --- | --- |
| Stepped **surface** | **Rejected.** Reads as blocky at every resolution tested |
| Stepped **cave walls** | **Tolerable.** Enclosed lighting and rock read very differently from a stepped hillside |
| Smoothed caps | Wanted. Floors *and* ceilings — a stepped roof over a smooth floor reads worse than either alone |
| Column size | 0.5 m and finer look right; 0.125 m preferred |

**This is the hybrid outcome and it keeps everything already built:** heightfield surface,
spans only where tunnels exist.

### Why ramps need fine columns

A floor is flat within a column, so the step underfoot is slope × column size no matter how
precise the vertical coordinate is. Millimetre spans do not help. Over the test corridor's
3 m descent across 13 m of run:

| Column | Step underfoot |
| --- | ---: |
| 1 m | 0.231 m |
| 0.5 m | 0.115 m |
| 0.25 m | 0.058 m |
| 0.125 m | 0.029 m |

### Per-edit cost, measured

Smoothed caps, 4 m bricks, main-thread synchronous cooking, which is the worst case. Both
readings are the 4-brick worst case, where an edit lands on a brick corner and the halo
crosses a boundary in both axes.

| Column | Bite | Build | Cook | Total |
| --- | --- | ---: | ---: | ---: |
| 1 m | 1 m | 0.28 ms | 0.27 ms | **0.55 ms** |
| 0.125 m | 0.25 m | 9.78 ms | 3.45 ms | **13.23 ms** |

Cost per brick scales as **columns per brick to the power 0.76** — sublinear, because much of
a brick is interior with no faces to emit. Fitting the two measurements:

| Column | Columns per brick | ms per brick | 4 bricks | Ramp step underfoot |
| --- | ---: | ---: | ---: | ---: |
| 1 m | 16 | 0.14 | 0.55 | 0.231 m |
| 0.5 m | 64 | 0.40 | 1.59 | 0.115 m |
| **0.25 m** | 256 | 1.15 | **4.58** | **0.058 m** |
| 0.125 m | 1024 | 3.31 | 13.23 | 0.029 m |

The middle two rows are interpolated, not measured.

A 0.25 m bite spans 2 columns; with a one-column halo the window is 4 columns, so against a
32-column brick the mix is 1 brick 76.6%, 2 bricks 21.9%, 4 bricks 1.6% — **1.27 bricks on
average**. At W3's 32 strokes/s that gives 47 ms/s of main-thread terrain work at 0.25 m and
16 ms/s at 0.5 m. Both are comfortably inside the acceptance spec's per-tick budget; 0.125 m
is not.

**0.25 m looks like the landing zone.** Its ramp step is 5.8 cm, which should be below the
threshold of feel, and its worst-case edit is under 5 ms with no off-thread work at all. It
also divides the 1 m interaction cell exactly 4 × 4.

Moving the cook off-thread via `Physics.BakeMesh` remains available and would take roughly a
quarter off; moving the mesh build off-thread too leaves only the install on the main thread.
Neither is needed at 0.25 m on these numbers.

### Rebuilding only what changed is most of the cost

The first version rebuilt a blanket 3×3 of bricks per edit and cost **26 ms**. Testing whether
a brick's columns or its one-column halo actually overlap the change brought the same edit to
4.28 ms — a **6× improvement from doing no extra work**, before any optimisation.

Bite size barely affects cost. The rebuild unit is the brick, and a 4 m brick holds 16 columns
at 1 m and 1,024 at 0.125 m.

### Two bugs worth remembering

Smoothed caps tore open and you could see through the world. It took two passes, because
there were two separate causes and fixing the first hid the second.

**Asymmetric corners.** Two adjacent columns computed *different heights for the same shared
corner*, because the rule was "average neighbours within the threshold of *my* height" and
each column measured that window from itself. The four columns meeting at a corner are the
same four whichever asks, so the answer must depend only on that set. Fixed by sorting the
gathered heights, splitting at any gap wider than the threshold, and taking the mean of the
group the caller falls in.

**This is the same widest-gap clustering as `CellGrid.SharedCornerRaw`.** Two independent
smoothing problems, same answer — worth reaching for first next time.

**Only half the wall edges followed their caps.** A wall edge has to land exactly on whichever
cap bounds it, and there are four ways that happens: this span's own open top, its own open
bottom (a ceiling), and the same two against the neighbour's caps. The first pass handled the
two top cases and neither bottom case — and ceiling smoothing was added in the same round,
which tilted every tunnel roof away from the raw boundary the walls were still stopping at.
So half the fix and half a new instance of the same fault shipped together.

The lesson is the shape of the fault, not the arithmetic: **whenever geometry is derived
per-element, every shared edge needs both sides asking the same question.** Both bugs, and the
cut/fill accounting bugs before them, are that same failure.

### Not attempted

Wall smoothing. Moving a wall horizontally is dual-contouring territory, not a cap tilt, and
the cubed walls were judged acceptable.

---

## D6 — The two representations join at a ceded brick, and the seam is exact

The appearance gate rejected spans as a surface. Two screenshots settled it: at 0.125 m
the hill is a corrugated cone, and at 1 m it is a ziggurat. The requirement is a realistic
terrain look, and blocky ground breaks it outright — so the hybrid is not a preference,
it is the only option left.

**Ownership.** The cell grid owns the surface everywhere. The span grid owns nothing until
something is dug, and from then on owns whole 4 m bricks — surface included — for as long
as a void exists inside them. A brick in that state is *ceded*: the surface mesher skips
its cells and the span mesher draws them.

Ceding a brick rather than a column is deliberate. The boundary is a straight line on a
coarse grid instead of a ragged outline that moves with every swing of a pick, and because
the seam is exact its position does not matter visually. **Coarse and exact beats tight
and approximate.**

### Why the seam costs nothing

The surface is not a formula. Each cell fans eight triangles from its centre to a ring of
eight boundary points — four corners and four edge midpoints — so it is a piecewise-linear
function that can be evaluated anywhere. `CellSurface` does exactly that, mirroring
`CellMeshBuilder` point for point.

Two properties make the join free rather than approximate:

1. **The ring is straight from a corner to the next edge midpoint.** Sampling a cell
   boundary at a quarter, a half or three quarters lands exactly on the line the surface
   mesher already draws. A ceded patch's outer edge is therefore the same edge, subdivided.
2. **A cell creases along both of its diagonals**, because the fan radiates from the
   centre. At four columns per cell those creases run along column diagonals, so a column
   sitting on one has to fold the same way the cell does.

Property 2 was a real defect, not a theoretical one. `SpanMeshBuilder.Cap` always split its
quad south-west to north-east, which is right for the columns on the main diagonal and
wrong for the four on the anti-diagonal. Measured against synthetic cells:

| Cap fold | Worst departure from the original surface |
| --- | ---: |
| Always south-west to north-east | **3.27 m** |
| Chosen per column | **3.6e-15 m** |

So the claim is not that a ceded patch is close to the surface. It is the same surface,
subdivided sixteen ways, to floating point. The scene measures this at startup over every
ceded column and prints it.

### The one hook

`SpanMeshBuilder.Corner` — the clustering rule that decides a smoothed cap's corner height
— defers to the surface whenever a cap sits at ground level. One line, in one place, and it
propagates to both things that need it: the cap itself, and the foot of a neighbouring wall
that has to land on that cap. That is the same lesson as both mesh tears: **whenever
geometry is derived per element, every shared edge has to ask one question.**

### What the surface and the spans each keep

| | Owner | Notes |
| --- | --- | --- |
| Ground height | cell grid | 1 m cells, unchanged, including flatten and the terracing release |
| Layered materials | span grid | lowering exposes what is underneath, which falls out of trimming the top rather than needing a rule |
| Voids | span grid | 0.25 m columns, millimetre heights |
| Geology | neither | computed from position; columns materialise on first touch |

Columns are **not** stored for untouched ground. At 0.25 m over 64 x 64 m there are 65,536
of them; the scene materialises only those inside a ceded brick and a one-column halo. That
is the "compute the geology, store the deviations" principle actually exercised rather than
asserted.

### The portal, and why it matters

A level adit driven into a rising slope has no usable mouth — the roof only thickens well
inside the hill, so the entrance ends up buried and you drop into it rather than walk in.
The scene answers it the way a real mine does: **the surface model cuts a bench, and the
tunnel starts from its face.** The bench cells are flattened, so the face is a genuine
vertical wall drawn by the surface mesher, and the mouth is a hole punched in that wall by
the span mesher. It is the hardest case for the seam and it is the first thing to look at.

That division of labour is the design in one sentence: **the shovel shapes open ground, the
pick goes underground.**

### The roof rule

Lowering ground until it meets a tunnel is a real event needing real rules — collapse, or a
hole you can fall down. Until those exist, a surface edit leaving less than 0.4 m over a
void is refused. The check runs after the command and undoes it, rather than predicting
derived corners two cells out beforehand: a duplicate rule drifts out of step with the rule
it duplicates.

### The bug that hid the whole thing first time

The hybrid scene silently grew a second world. `P0Bootstrap` carries a
`RuntimeInitializeOnLoadMethod` that spawns it into any scene without one — which is what
makes the plain demo need no setup at all — and it stood down for `SpanGateBootstrap` by
name. A third bootstrap was written and never added to that list.

The symptoms all looked like geometry faults and none of them were: two HUDs printing over
each other, `M` appearing to change the terrain (it was the other bootstrap's model toggle),
brick-shaped mottling (z-fighting between the two surfaces), and — the expensive one —
**no dug hole ever becoming visible, because an intact surface mesh sat coplanar over every
one of them.**

It also could not be defended against from the hybrid's side: `AfterSceneLoad` runs after
the scene's own `Awake`, so a scene bootstrap looking for the intruder finds nothing,
because it does not exist yet.

Fixed with an `IWorldBootstrap` marker interface that the auto-spawn defers to. **A guard
that enumerates the things it must know about goes stale the first time somebody adds one
without reading it; a guard things opt into does not.**

### The terrain read as blocky because of shading, not geometry

The first look at the hybrid surface was rejected as too faceted, with a diamond pattern
across every hill. The instinct was that the eight boundary points per cell were not
enough.

They were not the problem, and adding more could not have helped: a cell stores ONE height,
and all nine of a fan values are derived from it, so extra boundary points would only
interpolate information that is not there.

The cause was in CellMeshBuilder. Every triangle carried a single face normal, so every
facet was a visible step in the lighting and the fan topology showed through as a diamond.
Nothing was smooth-shaded.

Vertex normals now live in CellSurface next to the heights, keyed by position AND height:
two cells agreeing at a shared point blend, two disagreeing keep their hard edge. That is
the same test the mesh already uses to decide where to put a vertical face, so shading and
geometry cannot drift apart, and a levelled pad keeps its crisp rim.

**The span mesher reads the same normals.** A ceded cap that matched the surface
geometrically but shaded off its own flat faces would have announced itself as a patch of
differently-lit hillside -- the seam solved in position and handed straight back in light.

Remaining honest gap: the demo renders untextured flat colour, and a flat colour is what
makes every remaining facet visible. A scene Terrain -- a Gaia build, say -- is now imported
if one is present, and its colouring baked to a world-XZ texture that lands identically on
both meshers because both write world-XZ UVs.

### The roof rule refuses edits, it does not lock ground

First cut tested the resulting roof thickness on its own and refused anything under 0.4 m.
That locked out far more ground than it protected. A resync reaches two cells in every
direction, so one thin roof anywhere in that window vetoed every edit near it -- including
RAISING ground, which thickens the roof, and including cells whose surface the edit never
moved.

Now only a cut that makes a roof worse is refused: a column that is not being lowered
cannot break, and one that is gets judged on what it actually leaves. Improve-or-leave-alone
is the same shape as the existing step limits, and unlike a radius it needs no tuning.

That was still not enough, because of what counts as a roof. Cutting a cube down from open
ground leaves a few centimetres of solid over air all round the rim, wherever the ground was
higher than the top of the cube. Those slivers are geometrically real but they are the lip
of a pit, not a tunnel roof -- and every surface dig made a ring of them, each vetoing
lowering two cells out in every direction. So a roof thinner than the minimum is not
defended at all. A real roof may not be cut below the threshold; a lip can be shovelled
away, and the hole tidies up instead of freezing the ground around it.

MinRoof is 0.4 m and is a placeholder for rules that do not exist yet -- collapse, or a hole
you can fall down. It is one constant in HybridWorld.

### Where they genuinely disagree

**Mining downward from open ground.** The pick cuts the column below the surface, and the
cell grid still holds the old height. That mismatch is deliberately kept — it is what tells
the mesher a cap is dug rather than natural, and what stops a terraform edit two cells away
from quietly filling the pit back in. But it means cell height is no longer the truth about
that column, so cut/fill accounting and any future rule reading cell height would be wrong
there.

The likely resolution is a rule rather than a mechanism: **surface material is the shovel's
to remove, not the pick's.** That is worth deciding deliberately rather than discovering.

---

## Corrections on record

Recorded so they are not re-argued. All four are cases where a plausible inference was
stated as a conclusion.

| Claim | Status |
| --- | --- |
| "Digger most likely fails at the accounting gate" | **Overstated.** Undocumented is not absent. The installed edition and its extension points were never inspected. |
| "Digger's serialisation is an immutable package-wide ceiling" | **Overstated.** Whether independent instances can run concurrently is unknown, and the package may ship source. |
| "The server does no meshing" | **Overstated.** True of *render* meshing. With `MeshCollider` terrain the server still needs collision geometry and its acceleration structures. |
| "Divide the HUD collider time by 16 for a per-tile estimate" | **Wrong.** Area ratios only hold at constant resolution. One 16 × 16 m tile at 0.25 m has 4,096 columns — the same as the entire current 64 × 64 m map at 1 m. At 0.125 m it is 16,384. |

Two further calibrations worth keeping:

- **Region-partitioned concurrency helps least where it is needed most.** Independent
  preparation only helps when dependencies allow it, and a crowded shared mine is exactly
  the case where they do not. That is W3's second layout.
- **Direct span collision splits in two.** Point containment, ray queries and placement
  overlap are cheap and worth doing. Replacing `CharacterController` is a movement-system
  project — sliding, steps, slopes, contact tolerances, coherence with buildings — and gets
  worse if the client displays a smoothed surface the server does not have. Only if
  profiling forces it.

---

## Open questions

| Question | Why it matters |
| --- | --- |
| Cell model or vertex model? | The playtest answers it. It now also constrains the Digger option — see the constraint below. |
| Is the stepped span mesher visually acceptable? | Kills or clears D3. Cheapest gate, highest risk. |
| Is `RemovedMatterQuantity` proportional to volume? | Decides whether Digger's callback is an accounting primitive or a scalar-field statistic. |
| What does one collision bake cost at the intended resolution? | The likely throughput constraint, and currently unmeasured. |
| Tunnels in game one — yes or no? | Currently "maybe". If no, the heightfield already in place may simply be the answer. |
| Sluffing behaviour when every destination is blocked | Partial placement with the remainder kept in inventory, or an explicit loose pile. "The action succeeds" cannot mean deleting surplus. |

### The constraint that couples two of these

**Digger needs Unity Terrain. Unity Terrain is a vertex heightfield — one value per vertex,
no per-cell centre. So the cell model cannot drive it.**

If testers prefer the cell model *and* Digger tunnels are wanted, those are incompatible and
one of them gives. Worth knowing before the survey returns rather than after.

---

## Agreed sequence

Nothing below needs Digger, Fusion, or a dedicated server until step 6.

1. **Inspect what already exists.** HUD timing boundaries — what the collider timer actually
   includes — plus real triangle counts and the corrected cut/fill figures.
2. **Custom appearance test.** Stepped spans at 0.25 m and 0.125 m, dropped into the existing
   scene, keeping the controller, importer, lighting and 1 m interaction scale.
   **Time `Physics.BakeMesh` on that same geometry** — the appearance gate and the collision
   measurement are one experiment, and it is the only way to get a bake cost at the real
   resolution rather than by extrapolating from the wrong one.
3. **One correct edit path.** Soil over rock, limited inventory, redistribution, matching
   collision. No networking.
4. **Headless smoke test.** That exact path, in a dedicated-server build, no camera.
5. **Local W3 with moving actors.** 32 strokes/s, with accounting, collision updates and
   representative movement and tool queries — not just span writes.
6. **Small real-network test**, then joins, persistence and soak as results justify.

Steps 1–5 can establish a plausible measured path to 64-player terrain, or expose a reason
to stop. They cannot settle the complete 64-player question, which needs network and
integrated-game testing.

---

## Constraints to carry forward

- **Built-in render pipeline.** Every material here is built in code against Built-in shader
  names. Do not let any package installer convert the project to URP.
- **`Physics.BakeMesh` runs from jobs** provided the same mesh is not baked concurrently;
  colliders install on the main thread afterwards. The open question is whether that
  pipeline is sufficient, not whether off-thread baking exists.
- **Interaction scale is 1 m and stays 1 m**, independent of storage resolution.
- **Step limits bind players, not world generation.** An already-excessive step may be
  reduced but not worsened. This is implemented here and absent from the external specs.
- **`BuildPrep` removes entries from Always Included Shaders** on every build. It should not
  touch a vendor package's shaders, but it is the first place to look if something renders
  correctly in the editor and wrongly in a build.
