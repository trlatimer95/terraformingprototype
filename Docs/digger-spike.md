# Digger spike

Two small, separate tracks. Neither blocks the main sequence in `decisions.md`, and neither
is a backend qualification — Astra's benchmark is now a backend-neutral acceptance spec, not
a programme to run against Digger first.

| Track | Where it lives | What it answers |
| --- | --- | --- |
| **1. Vanilla Digger** | A throwaway project, outside this repo | What is this package like, and does it meet my needs on its own? |
| **2. Ours + theirs** | A branch of this prototype | Can our terraforming edit heights at runtime while Digger owns caves underneath? |

**Hard stop: two sessions per track.** If a track is not walking around a cave by the end of
its second session, record what blocked it and stop. A clean negative is worth as much as a
success and costs far less.

---

## Shared prerequisites

- [ ] **Confirm the edition.** Runtime editing requires Digger **PRO**. Plain Digger is
      editor-only, which makes both tracks pointless. Check before anything else.
- [ ] **Confirm the Unity version is one the installed release supports.** Do not pick a
      newer editor merely because it exists.
- [ ] **Disable automatic floating-voxel removal.** It deletes material outside any ledger.
- [ ] **Disable LOD generation initially.** Runtime LOD regeneration is documented as
      expensive and is noise while establishing whether the basics work.

### The trap that would cost a day

**Do not let any setup wizard convert a project to URP.** This prototype builds every
material in code against Built-in shader names — `Standard`, `Unlit/Color`,
`Nature/Terrain/Standard`. A pipeline switch turns the whole world magenta and the failure
reads as a Digger bug rather than a pipeline change.

This matters for Track 2 especially. If Digger only supports URP for the feature you want,
that is a finding: write it down rather than converting.

### One local interaction

`Assets/Editor/BuildPrep.cs` runs on every build and *removes* entries from Always Included
Shaders. It targets specific built-in names and anything in `unity default resources`, so it
should not touch a vendor package's shaders — but if Digger renders correctly in the editor
and wrongly in a build, look there first.

The first dig in the editor can stall while Burst compiles. Documented and expected; do not
record it as a performance result.

---

## Track 1 — vanilla Digger, on its own

A separate throwaway project. The point is familiarity: use the package the way its
documentation intends, with none of this project's ideas imposed on it, and form a first-hand
opinion about whether it covers what you want by itself.

- [ ] Fresh project. Plain Unity Terrain, 32 × 32 m, flat at 5 m, one visible material.
- [ ] `Tools > Digger > Setup terrains`, then `Tools > Digger > Setup for runtime`.
- [ ] Dig a cave mouth into a slope. Walk into it.
- [ ] Dig a second route until the two connect. Look at the junction.
- [ ] Add material above the original surface, not only inside an existing hole.
- [ ] Save, restart the editor, confirm the cave survives.
- [ ] Make a **packaged build** and repeat every check. Editor success is not the result.

Judge it as a user, not as an integrator: how the caves look up close, how the tools feel,
how much authoring work a decent-looking mine takes, and whether the terrain still reads as
terrain around an opening.

### Two measurements worth taking while it is running

These decide whether the completion callback is usable as an accounting primitive at all.
About half an hour once a cave exists, and they save the entire Gate A programme if either
comes back negative.

**1. Is the reported quantity proportional to volume?**
Cut a known volume — a 1 m cube — and record `RemovedMatterQuantity`. Cut the same volume as
a long shallow trench. Compute value ÷ m³ for both.

- Stable across shapes → possibly calibratable into a real quantity.
- Differs → it is a scalar-field statistic, not a volume, and accounting needs actual spatial
  readback.

**2. Does digging empty space report zero?**
Cut the same already-hollow space three times. Anything non-zero on the second or third means
a player could stand in a cave swinging at air for credit.

Record the numbers even if they are inconvenient.

---

## Track 2 — our terraforming, their mining

A branch of this repo, own scene. This is the higher-value experiment because it is the one
Astra's guide explicitly flagged as undocumented:

> *"Do not independently modify Unity height samples underneath existing Digger caves… the
> reviewed documentation does not establish a safe contract for simultaneous independent
> runtime heightfield and cave edits."*

That is not a reason to avoid it. **That is the test.** If it works, it is the ideal answer
for the smaller game: keep everything already built, add tunnels. If it breaks, tunnels mean
replacing the terraforming — which is the decision being settled.

### Why this is a day, not a fortnight

`UnityTerrainView` already mirrors the vertex model into a **real runtime Unity Terrain** and
re-syncs on every edit. That is what the `T` key toggles. So this is not a port; it is
pointing Digger at a terrain the existing tools already edit at runtime.

Same repo, own branch, own scene. A separate project would throw away `UnityTerrainView`, the
controller, the importer and the HUD — which is exactly what makes this cheap.

### The constraint

**Only the vertex model can take part.** Unity Terrain is a heightfield with one value per
vertex and no per-cell centre, so the cell model cannot drive it. If the playtest prefers the
cell model *and* Digger tunnels are wanted, those are incompatible and one gives.

### Sequence

- [ ] Branch. Scene with the vertex model driving `UnityTerrainView`, Digger set up on that
      same terrain.
- [ ] Dig a cave under a patch of ground, then **raise and lower that ground from above**
      using the existing tools. Does the cave survive? Does the roof thin correctly, or does
      the terrain resync wipe it?
- [ ] Flatten a pad directly over the cave roof. Same question.
- [ ] Open the cave to the surface from below and check the mouth from both sides.
- [ ] Save, restart, and confirm heights and caves restore together.
- [ ] Packaged build, repeat.

The failure to watch for is not a crash. It is the two representations silently disagreeing
about which material still exists — a cave that reappears after a height edit, a roof that
does not thin when the ground above is lowered, or a hole in the terrain with solid Digger
geometry still behind it.

---

## What neither track decides

Accounting, headless operation, throughput and multiplayer suitability. A good result here
does not clear those, and a bad result does not prove custom terrain would be faster or
better looking.

Track 1 answers "do I like this package and does it cover my needs alone." Track 2 answers
"can it coexist with what I have." Those are the two questions actually open.
