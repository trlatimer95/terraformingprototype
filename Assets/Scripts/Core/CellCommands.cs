using System.Collections.Generic;
using UnityEngine;

namespace Terraform.Core
{
    /// <summary>
    /// Command over the per-cell grid. Same contract as the vertex model: Validate is
    /// pure so a headless server can run it, and commands carry intent rather than a
    /// list of resulting heights.
    /// </summary>
    public interface ICellCommand
    {
        bool Validate(CellGrid grid);
        void Apply(CellGrid grid);
        void Revert(CellGrid grid);
        float SignedVolume { get; }
        string Describe();
    }

    /// <summary>
    /// Base for anything that changes one cell. Undo, and the cut/fill tally, are handled
    /// once here.
    ///
    /// Volume is measured over the 3x3 neighbourhood rather than the cell alone: moving a
    /// cell's centre changes the shared corner averages, which tilts all eight neighbours.
    /// Counting only the target would under-report every op.
    /// </summary>
    public abstract class CellCommand : ICellCommand
    {
        public readonly int Cx;
        public readonly int Cz;

        /// <summary>Max height difference allowed against an orthogonal neighbour. 0 = off.</summary>
        public readonly int MaxStepUnits;

        ushort _oldHeight;
        bool _oldFlat;
        bool _applied;
        float _volume;

        protected CellCommand(int cx, int cz, int maxStepUnits)
        {
            Cx = cx;
            Cz = cz;
            MaxStepUnits = maxStepUnits;
        }

        public float SignedVolume { get { return _volume; } }

        public virtual bool Validate(CellGrid grid)
        {
            return grid.InBounds(Cx, Cz);
        }

        /// <summary>
        /// Angle-of-repose guard. Ground will not stand more than this far above the cell
        /// beside it -- past that the side wall is taller than any texture can cover and
        /// it stops reading as terrain.
        /// </summary>
        protected bool StepAllowed(CellGrid grid, int newRaw)
        {
            if (MaxStepUnits <= 0) return true;

            return Near(grid, newRaw, Cx - 1, Cz)
                && Near(grid, newRaw, Cx + 1, Cz)
                && Near(grid, newRaw, Cx, Cz - 1)
                && Near(grid, newRaw, Cx, Cz + 1);
        }

        bool Near(CellGrid grid, int newRaw, int nx, int nz)
        {
            if (!grid.InBounds(nx, nz)) return true;

            int neighbour = grid.GetRaw(nx, nz);
            int after = Mathf.Abs(newRaw - neighbour);
            if (after <= MaxStepUnits) return true;

            // Generated ground arrives with steps steeper than the guard allows -- cliffs
            // the player did not make. Refusing outright there would leave a whole
            // mountainside no tool could touch, so an edit that does not make a bad step
            // worse goes through. The high side always keeps a legal move, which is how
            // terracing works anyway: start at the top and walk it down.
            return after <= Mathf.Abs(grid.GetRaw(Cx, Cz) - neighbour);
        }

        protected abstract void Mutate(CellGrid grid);

        public void Apply(CellGrid grid)
        {
            _oldHeight = grid.GetRaw(Cx, Cz);
            _oldFlat = grid.IsFlat(Cx, Cz);

            float before = RegionVolume(grid);
            Mutate(grid);
            _volume = RegionVolume(grid) - before;

            _applied = true;
        }

        public void Revert(CellGrid grid)
        {
            if (!_applied) return;
            grid.SetRaw(Cx, Cz, _oldHeight);
            grid.SetFlat(Cx, Cz, _oldFlat);
            RevertExtra(grid);
        }

        /// <summary>Undo anything a command changed beyond its own cell.</summary>
        protected virtual void RevertExtra(CellGrid grid) { }

        /// <summary>
        /// Half-width of the region this op can disturb, for the cut/fill tally.
        ///
        /// Two, not one. A shared corner normally reaches only the eight neighbours, but
        /// SharedCornerRaw's diagonal tie-break consults NeighbourhoodMeanRaw over a 4x4
        /// block of cells, so moving one centre can flip a corner two cells away. Measured
        /// against a whole-grid recount over 4,000 randomised grids, a 3x3 region
        /// under-reported by up to 1.025 m3; 5x5 is exact.
        /// </summary>
        protected virtual int Radius { get { return 2; } }

        float RegionVolume(CellGrid grid)
        {
            float total = 0f;

            int r = Radius;

            for (int j = -r; j <= r; j++)
                for (int i = -r; i <= r; i++)
                {
                    int cx = Cx + i;
                    int cz = Cz + j;
                    if (grid.InBounds(cx, cz)) total += grid.CellVolume(cx, cz);
                }

            return total;
        }

        public abstract string Describe();
    }

    /// <summary>
    /// Raise or lower one cell's centre.
    ///
    /// Only this cell's stored height moves. Its corners are averages, so they rise a
    /// quarter as much and the neighbours simply tilt to meet it -- the pile stays inside
    /// the cell, and piling higher makes a taller pyramid rather than a wider one.
    ///
    /// Sculpting clears the flattened flag: a cell you have just dug is no longer level,
    /// so it goes back to meshing with its neighbours until you flatten it again.
    /// </summary>
    public sealed class CellSculptCommand : CellCommand
    {
        public readonly int DeltaUnits;

        public CellSculptCommand(int cx, int cz, int deltaUnits, int maxStepUnits)
            : base(cx, cz, maxStepUnits)
        {
            DeltaUnits = deltaUnits;
        }

        public override bool Validate(CellGrid grid)
        {
            if (!base.Validate(grid)) return false;

            int target = grid.GetRaw(Cx, Cz) + DeltaUnits;
            if (target < 0 || target > CellGrid.MaxRaw) return false;

            return StepAllowed(grid, target);
        }

        protected override void Mutate(CellGrid grid)
        {
            int target = Mathf.Clamp(grid.GetRaw(Cx, Cz) + DeltaUnits, 0, CellGrid.MaxRaw);
            grid.SetRaw(Cx, Cz, (ushort)target);
            grid.SetFlat(Cx, Cz, false);
        }

        public override string Describe()
        {
            float metres = DeltaUnits * CellGrid.MetresPerUnit;
            return string.Format("cell ({0},{1}) {2}{3:0.###} m", Cx, Cz, DeltaUnits >= 0 ? "+" : "", metres);
        }
    }

    /// <summary>
    /// Level one cell: pin its four corners to its own centre height.
    ///
    /// The height is always the cell's own, read at the moment the command runs. An
    /// earlier version could carry a held datum and move the cell to that height first,
    /// which turned out to be a trap: the datum was captured on a right-click and then
    /// silently outlived whatever the player did next, so sculpting a cell and levelling
    /// it snapped it back to a height chosen minutes earlier. Levelling now only ever
    /// makes a cell rigid where it already stands.
    /// </summary>
    public sealed class CellFlattenCommand : CellCommand
    {
        /// <summary>Height it went rigid at, for the log. Only meaningful once applied.</summary>
        ushort _levelledAt;

        public CellFlattenCommand(int cx, int cz, int maxStepUnits)
            : base(cx, cz, maxStepUnits)
        {
        }

        public override bool Validate(CellGrid grid)
        {
            if (!base.Validate(grid)) return false;

            // No height changes, so the step rule cannot be broken and the only question
            // is whether anything would actually happen. A cell that is already rigid
            // still has work to do if a neighbouring pad is sitting at a different height.
            return !grid.IsFlat(Cx, Cz) || HasMismatchedFlatNeighbour(grid);
        }

        bool HasMismatchedFlatNeighbour(CellGrid grid)
        {
            ushort here = grid.GetRaw(Cx, Cz);

            for (int i = 0; i < StepX.Length; i++)
            {
                int nx = Cx + StepX[i];
                int nz = Cz + StepZ[i];

                if (!grid.InBounds(nx, nz)) continue;
                if (grid.IsFlat(nx, nz) && grid.GetRaw(nx, nz) != here) return true;
            }

            return false;
        }

        // The whole ring, diagonals included. A diagonal pad shares only a corner, but
        // that corner is enough: SharedCornerRaw lifts it to the flattened cell's height,
        // which leaves a face on the ordinary cells sitting between the two pads.
        static readonly int[] StepX = { -1, 1, 0, 0, -1, 1, -1, 1 };
        static readonly int[] StepZ = { 0, 0, -1, 1, -1, -1, 1, 1 };

        /// <summary>Which neighbours gave up their flattened flag, for undo.</summary>
        readonly bool[] _released = new bool[8];

        /// <summary>
        /// Hand back the flag a neighbouring pad was holding, when it sits at a different
        /// height.
        ///
        /// A flattened cell keeps its corners rigid, and that rigidity is exactly what puts
        /// a vertical face between it and anything lower. Two pads side by side at
        /// different heights therefore meet in a wall, and a wall is a strip of ground
        /// texture stretched over a face that has no width -- the thing terracing looks
        /// worst doing.
        ///
        /// So the newer pad wins. The older one goes back to blending, its edge drops to
        /// meet the new pad, and the join becomes a slope with no face at all. Neighbours
        /// at the SAME height keep their flags, so building a pad cell by cell still ends
        /// with one rigid pad instead of dissolving as it grows.
        ///
        /// Diagonals count. Two pads meeting only at a corner still raise that corner to
        /// the flattened height, and the face then lands on the ordinary cells between
        /// them rather than between the pads -- the same stretched strip, just displaced.
        /// </summary>
        void ReleaseMismatchedNeighbours(CellGrid grid)
        {
            ushort here = grid.GetRaw(Cx, Cz);

            for (int i = 0; i < StepX.Length; i++)
            {
                _released[i] = false;

                int nx = Cx + StepX[i];
                int nz = Cz + StepZ[i];

                if (!grid.InBounds(nx, nz)) continue;
                if (!grid.IsFlat(nx, nz)) continue;
                if (grid.GetRaw(nx, nz) == here) continue;

                grid.SetFlat(nx, nz, false);
                _released[i] = true;
            }
        }

        protected override void RevertExtra(CellGrid grid)
        {
            for (int i = 0; i < StepX.Length; i++)
                if (_released[i]) grid.SetFlat(Cx + StepX[i], Cz + StepZ[i], true);
        }

        int ReleasedCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < StepX.Length; i++) if (_released[i]) n++;
                return n;
            }
        }

        protected override void Mutate(CellGrid grid)
        {
            _levelledAt = grid.GetRaw(Cx, Cz);
            grid.SetFlat(Cx, Cz, true);

            ReleaseMismatchedNeighbours(grid);
        }

        public override string Describe()
        {
            string where = string.Format("flatten ({0},{1}) at {2:0.###} m", Cx, Cz,
                CellGrid.ToMetres(_levelledAt));

            int released = ReleasedCount;
            if (released == 0) return where;

            // Worth naming in the log: releasing a neighbour softens ground the player
            // levelled earlier, and that is surprising if it is not said out loud.
            return where + string.Format(", sloped {0} neighbour{1}",
                                         released, released == 1 ? "" : "s");
        }
    }

    /// <summary>
    /// The single funnel for cell mutation, and the seed of the save format and wire
    /// protocol. Undo is a side effect of getting that right.
    /// </summary>
    public sealed class CellCommandLog
    {
        readonly List<ICellCommand> _done = new List<ICellCommand>();
        readonly List<ICellCommand> _undone = new List<ICellCommand>();

        public int DoneCount { get { return _done.Count; } }
        public int UndoneCount { get { return _undone.Count; } }
        public float NetVolume { get; private set; }
        public string LastDescription { get; private set; }

        public bool Execute(ICellCommand cmd, CellGrid grid)
        {
            if (!cmd.Validate(grid)) return false;

            cmd.Apply(grid);
            _done.Add(cmd);
            _undone.Clear();
            NetVolume += cmd.SignedVolume;
            LastDescription = cmd.Describe();
            return true;
        }

        public bool Undo(CellGrid grid)
        {
            if (_done.Count == 0) return false;

            ICellCommand cmd = _done[_done.Count - 1];
            _done.RemoveAt(_done.Count - 1);
            cmd.Revert(grid);
            _undone.Add(cmd);
            NetVolume -= cmd.SignedVolume;
            LastDescription = "undo: " + cmd.Describe();
            return true;
        }

        public bool Redo(CellGrid grid)
        {
            if (_undone.Count == 0) return false;

            ICellCommand cmd = _undone[_undone.Count - 1];
            _undone.RemoveAt(_undone.Count - 1);
            if (!cmd.Validate(grid)) return false;

            cmd.Apply(grid);
            _done.Add(cmd);
            NetVolume += cmd.SignedVolume;
            LastDescription = "redo: " + cmd.Describe();
            return true;
        }

        public void Clear()
        {
            _done.Clear();
            _undone.Clear();
            NetVolume = 0f;
            LastDescription = null;
        }
    }
}
