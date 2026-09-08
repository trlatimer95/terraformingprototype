# Verification harnesses

Python replicas of the terrain rules, used to check the C# against a brute-force recount.

| Script | What it answers |
| --- | --- |
| `sim_terrace.py` | Does flattening beside an existing pad leave a vertical face? |
| `audit_volume.py` | Does the reported cut/fill figure equal the volume that actually changed? |

Run either directly; `audit_volume.py` loads `sim_terrace.py` for the shared grid model.

```bash
python audit_volume.py
```

## Why these exist, and what is wrong with them

They found two real bugs that reasoning had missed:

- The cell model's cut/fill region was 3×3, which under-reported by up to **1.025 m³**.
  `SharedCornerRaw`'s diagonal tie-break consults a 4×4 neighbourhood mean, so one edit
  can flip a corner two cells away. 5×5 is exact.
- The vertex model summed each vertex's own area × its height change, which silently
  averages the two possible diagonals of every quad while the mesh only ever builds one.
  Up to **2.03 m³** out on a single sculpt.

Both are now fixed and both audits report zero error over 4,000 randomised grids.

**These are replicas, and a replica can drift from the thing it replicates.** They are only
trustworthy while `CellGrid`, `HeightGrid` and the two mesh builders match what is encoded
here. If a corner rule changes in C# and not here, the audit will confidently verify
yesterday's design.

The right long-term home is Unity EditMode tests exercising the real types — the Test
Framework package plus an `EditMode` assembly referencing `Terraform.Core`. Until that
exists, re-read the C# alongside these before trusting a run.
