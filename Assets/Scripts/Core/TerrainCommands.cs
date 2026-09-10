using UnityEngine;

namespace Terraform.Core
{
    /// <summary>
    /// Every terrain mutation is a command. Validate() is pure and must never touch
    /// rendering, so a headless server can run the identical check later without a
    /// scene. Commands carry intent (a target and a rule), not a list of resulting
    /// heights, which keeps them small on the wire and lets a server re-derive them.
    ///
    /// Sculpt addresses a VERTEX (a point, so raising it makes a peak); flatten
    /// addresses a PRIMAL CELL (a quad, so levelling its four corners makes a flat
    /// face). Those are genuinely different units -- the grid overlay switches to match
    /// whichever tool is active rather than pretending otherwise.
    /// </summary>
    public interface ITerrainCommand
    {
        bool Validate(HeightGrid grid);
        void Apply(HeightGrid grid);
        void Revert(HeightGrid grid);

        /// <summary>Displaced volume in cubic metres. Positive = fill, negative = cut.</summary>
        float SignedVolume { get; }

        string Describe();
    }

    /// <summary>
    /// Base for anything that rewrites a rectangle of vertices. Subclasses only decide
    /// what each vertex should become; capture, clamping, volume accounting and undo
    /// are handled once here.
    ///
    /// Volume is measured as a before/after recount of every cell the moved vertices
    /// touch, using the mesh's own triangulation. Summing each vertex's own area instead
    /// looks simpler and is wrong: it silently averages the two possible diagonals of
    /// every quad, and the mesh only ever builds one of them. See HeightGrid.CellVolume.
    /// </summary>
    public abstract class VertexRectCommand : ITerrainCommand
    {
        public readonly int Vx0;
        public readonly int Vz0;
        public readonly int W;
        public readonly int H;

        ushort[] _old;
        float _volume;
        bool _applied;

        protected VertexRectCommand(int vx0, int vz0, int w, int h)
        {
            Vx0 = vx0;
            Vz0 = vz0;
            W = w;
            H = h;
        }

        public float SignedVolume { get { return _volume; } }

        public virtual bool Validate(HeightGrid grid)
        {
            if (W <= 0 || H <= 0) return false;
            if (!grid.InBounds(Vx0, Vz0)) return false;
            if (!grid.InBounds(Vx0 + W - 1, Vz0 + H - 1)) return false;

            // P1: angle-of-repose belongs here -- reject, or cascade a slump, when the
            // result would exceed the material's max step against a neighbour.
            return true;
        }

        /// <summary>Height this vertex should take. Return false to leave it alone.</summary>
        protected abstract bool TryTarget(HeightGrid grid, int vx, int vz, out int targetRaw);

        /// <summary>Optional pre-pass, for commands that need a whole-footprint figure first.</summary>
        protected virtual void Prepare(HeightGrid grid) { }

        public void Apply(HeightGrid grid)
        {
            Prepare(grid);

            _old = new ushort[W * H];

            float before = TouchedVolume(grid);

            for (int j = 0; j < H; j++)
            {
                for (int i = 0; i < W; i++)
                {
                    int vx = Vx0 + i;
                    int vz = Vz0 + j;

                    ushort old = grid.GetRaw(vx, vz);
                    _old[j * W + i] = old;

                    int target;
                    if (!TryTarget(grid, vx, vz, out target)) continue;

                    if (target < 0) target = 0;
                    if (target > HeightGrid.MaxRaw) target = HeightGrid.MaxRaw;
                    if (target == old) continue;

                    grid.SetRaw(vx, vz, (ushort)target);
                }
            }

            _volume = TouchedVolume(grid) - before;
            _applied = true;
        }

        /// <summary>
        /// Every cell with a corner in the rect. A cell's diagonal depends only on its own
        /// four corners, so nothing further out can change and this needs no margin.
        /// </summary>
        float TouchedVolume(HeightGrid grid)
        {
            float total = 0f;

            for (int cz = Vz0 - 1; cz < Vz0 + H; cz++)
                for (int cx = Vx0 - 1; cx < Vx0 + W; cx++)
                {
                    if (cx < 0 || cz < 0 || cx >= grid.CellsX || cz >= grid.CellsZ) continue;
                    total += grid.CellVolume(cx, cz);
                }

            return total;
        }

        public void Revert(HeightGrid grid)
        {
            if (!_applied) return;

            for (int j = 0; j < H; j++)
                for (int i = 0; i < W; i++)
                    grid.SetRaw(Vx0 + i, Vz0 + j, _old[j * W + i]);
        }

        public abstract string Describe();
    }

    /// <summary>
    /// Raise or lower a single vertex.
    ///
    /// This is the lump. Only the target moves; its neighbours keep their heights and
    /// simply tilt to meet it, because the four cells around a vertex are defined by
    /// their corners. Nothing tapers outward, so raising twice makes the peak twice as
    /// tall rather than dragging the surrounding ground up with it.
    ///
    /// MaxStepUnits is the angle-of-repose guard: material will not stand more than a
    /// given height above its neighbours. Zero disables it.
    /// </summary>
    public sealed class VertexAdjustCommand : VertexRectCommand
    {
        public readonly int Vx;
        public readonly int Vz;
        public readonly int DeltaUnits;
        public readonly int MaxStepUnits;

        /// <summary>
        /// 0 leaves a sharp pyramid. Above that, the eight surrounding vertices relax one
        /// step toward the mean of their own neighbours, which rounds the apex and the
        /// base of a pit.
        ///
        /// Relaxation rather than an additive taper, deliberately: smoothing is
        /// contractive, so it cannot push a vertex past the values around it however many
        /// times you click. An additive blend compounds and drags the whole neighbourhood
        /// upward on repeat.
        /// </summary>
        public readonly float Rounding;

        ushort[] _targets;
        bool[] _write;

        public VertexAdjustCommand(int vx0, int vz0, int w, int h,
                                   int vx, int vz, int deltaUnits, int maxStepUnits, float rounding)
            : base(vx0, vz0, w, h)
        {
            Vx = vx;
            Vz = vz;
            DeltaUnits = deltaUnits;
            MaxStepUnits = maxStepUnits;
            Rounding = rounding;
        }

        public override bool Validate(HeightGrid grid)
        {
            if (!base.Validate(grid)) return false;
            if (!grid.InBounds(Vx, Vz)) return false;

            int target = grid.GetRaw(Vx, Vz) + DeltaUnits;
            if (target < 0 || target > HeightGrid.MaxRaw) return false;

            if (MaxStepUnits <= 0) return true;

            // All eight, not just the four orthogonal ones. Checking only the axes leaves
            // the diagonals free to become cliffs: a vertex can be walled off from the four
            // it touches directly and still stand several metres above the four at its
            // corners, which is the same unsupported step turned forty-five degrees.
            for (int i = 0; i < 8; i++)
            {
                if (!Within(grid, target, Vx + StepX[i], Vz + StepZ[i], StepScale[i])) return false;
            }

            return true;
        }

        static readonly int[] StepX = { -1, 1, 0, 0, -1, 1, -1, 1 };
        static readonly int[] StepZ = { 0, 0, -1, 1, -1, -1, 1, 1 };

        /// <summary>
        /// How much further apart a neighbour is than an orthogonal one. A diagonal vertex
        /// sits root-two spacings away, so at the same SLOPE it tolerates root-two times the
        /// height difference. Using one height limit for both would make diagonals the
        /// strictest direction on the grid for no physical reason -- the same mistake as a
        /// fixed smoothing threshold across changing resolutions, rotated forty-five degrees.
        /// </summary>
        static readonly float[] StepScale =
        {
            1f, 1f, 1f, 1f,
            1.41421356f, 1.41421356f, 1.41421356f, 1.41421356f
        };

        bool Within(HeightGrid grid, int target, int nx, int nz, float scale)
        {
            if (!grid.InBounds(nx, nz)) return true;

            int limit = Mathf.RoundToInt(MaxStepUnits * scale);

            int neighbour = grid.GetRaw(nx, nz);
            int after = Mathf.Abs(target - neighbour);
            if (after <= limit) return true;

            // Same concession the cell model makes: ground that was generated steeper than
            // the guard allows can still be worked, as long as the edit does not deepen
            // the offending step. See CellCommand.Near.
            return after <= Mathf.Abs(grid.GetRaw(Vx, Vz) - neighbour);
        }

        /// <summary>
        /// Every target is solved up front from the pre-edit state. Computing them during
        /// the write loop would let earlier vertices feed later ones, making the result
        /// depend on iteration order -- fine locally, fatal once a server has to agree.
        /// </summary>
        protected override void Prepare(HeightGrid grid)
        {
            _targets = new ushort[W * H];
            _write = new bool[W * H];

            int peak = Mathf.Clamp(grid.GetRaw(Vx, Vz) + DeltaUnits, 0, HeightGrid.MaxRaw);

            for (int j = 0; j < H; j++)
            {
                for (int i = 0; i < W; i++)
                {
                    int vx = Vx0 + i;
                    int vz = Vz0 + j;
                    int index = j * W + i;

                    if (vx == Vx && vz == Vz)
                    {
                        _targets[index] = (ushort)peak;
                        _write[index] = true;
                        continue;
                    }

                    if (Rounding <= 0f) continue;

                    int sum = 0, n = 0;
                    Accumulate(grid, peak, vx - 1, vz, ref sum, ref n);
                    Accumulate(grid, peak, vx + 1, vz, ref sum, ref n);
                    Accumulate(grid, peak, vx, vz - 1, ref sum, ref n);
                    Accumulate(grid, peak, vx, vz + 1, ref sum, ref n);
                    if (n == 0) continue;

                    int existing = grid.GetRaw(vx, vz);
                    int relaxed = Mathf.RoundToInt(Mathf.Lerp(existing, sum / (float)n, Rounding));
                    if (relaxed == existing) continue;

                    _targets[index] = (ushort)Mathf.Clamp(relaxed, 0, HeightGrid.MaxRaw);
                    _write[index] = true;
                }
            }
        }

        /// <summary>Pre-edit height, with the peak already standing at its new value.</summary>
        void Accumulate(HeightGrid grid, int peak, int nx, int nz, ref int sum, ref int n)
        {
            if (!grid.InBounds(nx, nz)) return;
            sum += (nx == Vx && nz == Vz) ? peak : grid.GetRaw(nx, nz);
            n++;
        }

        protected override bool TryTarget(HeightGrid grid, int vx, int vz, out int targetRaw)
        {
            int index = (vz - Vz0) * W + (vx - Vx0);
            targetRaw = _targets[index];
            return _write[index];
        }

        public override string Describe()
        {
            float metres = DeltaUnits * HeightGrid.MetresPerUnit;
            return string.Format("({0},{1}) {2}{3:0.###} m", Vx, Vz, DeltaUnits >= 0 ? "+" : "", metres);
        }
    }

    /// <summary>
    /// Move every vertex in a rect by the same delta.
    ///
    /// Raising a whole cell keeps it flat as it rises, rather than tipping it the way
    /// moving one corner does. Because the four corners shift together their differences
    /// never change, so only the vertices OUTSIDE the rect can breach the repose limit --
    /// those are the only ones checked.
    /// </summary>
    public sealed class AdjustCellCommand : VertexRectCommand
    {
        public readonly int DeltaUnits;
        public readonly int MaxStepUnits;

        public AdjustCellCommand(int vx0, int vz0, int w, int h, int deltaUnits, int maxStepUnits)
            : base(vx0, vz0, w, h)
        {
            DeltaUnits = deltaUnits;
            MaxStepUnits = maxStepUnits;
        }

        bool Inside(int vx, int vz)
        {
            return vx >= Vx0 && vx < Vx0 + W && vz >= Vz0 && vz < Vz0 + H;
        }

        bool Clears(HeightGrid grid, int current, int target, int nx, int nz)
        {
            if (!grid.InBounds(nx, nz) || Inside(nx, nz)) return true;

            int neighbour = grid.GetRaw(nx, nz);
            int after = Mathf.Abs(target - neighbour);
            if (after <= MaxStepUnits) return true;

            return after <= Mathf.Abs(current - neighbour);
        }

        public override bool Validate(HeightGrid grid)
        {
            if (!base.Validate(grid)) return false;

            for (int j = 0; j < H; j++)
            {
                for (int i = 0; i < W; i++)
                {
                    int vx = Vx0 + i;
                    int vz = Vz0 + j;

                    int current = grid.GetRaw(vx, vz);
                    int target = current + DeltaUnits;
                    if (target < 0 || target > HeightGrid.MaxRaw) return false;

                    if (MaxStepUnits <= 0) continue;

                    if (!Clears(grid, current, target, vx - 1, vz)) return false;
                    if (!Clears(grid, current, target, vx + 1, vz)) return false;
                    if (!Clears(grid, current, target, vx, vz - 1)) return false;
                    if (!Clears(grid, current, target, vx, vz + 1)) return false;
                }
            }

            return true;
        }

        protected override bool TryTarget(HeightGrid grid, int vx, int vz, out int targetRaw)
        {
            targetRaw = grid.GetRaw(vx, vz) + DeltaUnits;
            return true;
        }

        public override string Describe()
        {
            float metres = DeltaUnits * HeightGrid.MetresPerUnit;
            return string.Format("cell {0}{1:0.###} m", DeltaUnits >= 0 ? "+" : "", metres);
        }
    }

    /// <summary>
    /// Set every vertex in a rect to one height.
    ///
    /// Addressed by PRIMAL cell: N cells span N+1 vertices, and writing all of them
    /// levels exactly that block -- no ring to grow, no diagonals to skip. Because
    /// neighbouring cells share these corners, adjacent pads knit together instead of
    /// leaving a seam at the edge.
    ///
    /// The target is passed in rather than sampled here, so the result does not change
    /// with sub-cell aim.
    /// </summary>
    public sealed class FlattenAreaCommand : VertexRectCommand
    {
        public readonly ushort TargetRaw;

        public FlattenAreaCommand(int vx0, int vz0, int w, int h, ushort targetRaw)
            : base(vx0, vz0, w, h)
        {
            TargetRaw = targetRaw;
        }

        protected override bool TryTarget(HeightGrid grid, int vx, int vz, out int targetRaw)
        {
            targetRaw = TargetRaw;
            return true;
        }

        public override string Describe()
        {
            return string.Format("flatten {0}x{1} cells to {2:0.###} m",
                W - 1, H - 1, HeightGrid.ToMetres(TargetRaw));
        }
    }

    /// <summary>
    /// Grade a corridor between two points: a planar ramp along the A-B axis, flat
    /// across it. This is flatten-with-slope -- the op that builds roads and the
    /// approach ramps between terraces.
    ///
    /// Vertices beyond the corridor half-width, or past either end, are left alone
    /// rather than extrapolated, so a ramp never disturbs ground outside itself.
    /// </summary>
    public sealed class RampCommand : VertexRectCommand
    {
        public readonly float Ax, Az, Bx, Bz;   // continuous grid coordinates
        public readonly ushort ARaw, BRaw;
        public readonly float HalfWidth;

        public RampCommand(int vx0, int vz0, int w, int h,
                           float ax, float az, ushort aRaw,
                           float bx, float bz, ushort bRaw,
                           float halfWidth)
            : base(vx0, vz0, w, h)
        {
            Ax = ax; Az = az; ARaw = aRaw;
            Bx = bx; Bz = bz; BRaw = bRaw;
            HalfWidth = halfWidth;
        }

        protected override bool TryTarget(HeightGrid grid, int vx, int vz, out int targetRaw)
        {
            targetRaw = 0;

            float abx = Bx - Ax;
            float abz = Bz - Az;
            float lenSq = abx * abx + abz * abz;
            if (lenSq < 1e-6f) return false;

            float t = Mathf.Clamp01(((vx - Ax) * abx + (vz - Az) * abz) / lenSq);

            // Distance measured at the clamped point, so the corridor has square ends
            // rather than bleeding past the anchors.
            float dx = vx - (Ax + abx * t);
            float dz = vz - (Az + abz * t);
            if (dx * dx + dz * dz > HalfWidth * HalfWidth) return false;

            targetRaw = Mathf.RoundToInt(Mathf.Lerp(ARaw, BRaw, t));
            return true;
        }

        public override string Describe()
        {
            float rise = (BRaw - ARaw) * HeightGrid.MetresPerUnit;
            float run = Mathf.Sqrt((Bx - Ax) * (Bx - Ax) + (Bz - Az) * (Bz - Az));
            float grade = run > 0.01f ? rise / run * 100f : 0f;
            return string.Format("ramp {0:0.##} m over {1:0.#} m ({2:0.#}%)", rise, run, grade);
        }
    }
}
