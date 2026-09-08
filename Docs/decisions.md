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
