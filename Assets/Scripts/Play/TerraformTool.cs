using UnityEngine;
using Terraform.Core;
using Terraform.View;

namespace Terraform.Play
{
    /// <summary>
    /// Aim from screen centre, act on the grid within arm's reach.
    ///
    /// One grid: lines through the vertices, cells between them.
    ///
    /// Sculpt raises a VERTEX -- a corner of that grid, marked with a dot, since a point
    /// is what it moves. Flatten levels the single CELL under the crosshair by setting its
    /// four corners to one height, outlined so you can see which quad you are levelling.
    ///
    /// So a pad is built the way a site actually is: raise the corners around it, then
    /// level the ground they enclose. Corners are shared, so adjacent pads knit together
    /// instead of stepping.
    /// </summary>
    public sealed class TerraformTool : MonoBehaviour
    {
        public enum Mode { Sculpt, Flatten, Ramp, Cell }

        /// <summary>Height increment per click, in fixed-point units. 20 == 1 m.</summary>
        public static readonly int[] StepChoices = { 1, 2, 5, 10, 20 };   // 5cm .. 1m

        /// <summary>Angle-of-repose guard: how far a vertex may stand above its neighbours. 0 = off.</summary>
        public static readonly int[] MaxStepChoices = { 10, 20, 30, 40, 60, 0 };   // 0.5m .. 3m

        /// <summary>How hard the ring relaxes after a raise. 0 leaves a sharp pyramid.</summary>
        public static readonly float[] RoundingChoices = { 0f, 0.15f, 0.3f, 0.5f };

        /// <summary>Ramp corridor width, in grid units.</summary>
        public static readonly int[] WidthChoices = { 1, 2, 3, 4, 6 };

        public ChunkView Chunk;
        public Camera Cam;
        public Collider PickCollider;

        /// <summary>Dot marking the vertex sculpt will move. Hidden when nothing is in reach.</summary>
        public Transform Marker;

        public float MaxReach = 5f;
        public int StepIndex = 1;                 // default 2 units = 10 cm

        // Shared by sculpt and cell raise: it is one physical constraint -- how far ground
        // will stand above the ground beside it -- so having each tool carry its own value
        // just means one of them silently blocks sooner than the other.
        public int MaxStepIndex = 4;              // 60 units = 3 m, matched to the cell model
        public int RampWidthIndex = 1;            // default 2 cells
        // Off by default: rounding relaxes the neighbouring vertices too, so a corner
        // would drift whenever you raised one beside it. A corner must hold its height
        // until the player moves it, which is what makes "raise the corners, then flatten
        // between them" a workflow you can trust.
        public int RoundingIndex = 0;
        public float RepeatInterval = 0.07f;

        public Mode Tool { get; private set; }

        public bool HasTarget { get; private set; }
        public int TargetVx { get; private set; }
        public int TargetVz { get; private set; }

        /// <summary>Primal cell under the crosshair -- the quad between four vertices.
        /// Sculpt addresses a vertex, flatten addresses this.</summary>
        public bool HasCell { get; private set; }
        public int TargetCx { get; private set; }
        public int TargetCz { get; private set; }

        public bool LastEditRejected { get; private set; }
        public int ActiveDirection { get { return _activeDir; } }

        public bool HasAnchor { get { return _hasAnchor; } }

        public int StepUnits { get { return StepChoices[Mathf.Clamp(StepIndex, 0, StepChoices.Length - 1)]; } }
        public float StepMetres { get { return StepUnits * HeightGrid.MetresPerUnit; } }

        public float Rounding { get { return RoundingChoices[Mathf.Clamp(RoundingIndex, 0, RoundingChoices.Length - 1)]; } }


        public int MaxStepUnits { get { return MaxStepChoices[Mathf.Clamp(MaxStepIndex, 0, MaxStepChoices.Length - 1)]; } }
        public int RampWidth { get { return WidthChoices[Mathf.Clamp(RampWidthIndex, 0, WidthChoices.Length - 1)]; } }


        float _repeatTimer;
        int _activeDir;

        bool _hasAnchor;
        int _anchorVx, _anchorVz;
        ushort _anchorRaw;

        void Update()
        {
            if (Chunk == null || Chunk.Grid == null || Cam == null) return;
            if (PickCollider == null) PickCollider = Chunk.GetComponent<Collider>();

            HandleModeKeys();
            CycleStep();
            UpdateTarget();
            HandleHistory();

            // A click while the cursor is free is a click to recapture it, not a dig.
            if (Cursor.lockState != CursorLockMode.Locked) { StopRepeat(); return; }

            switch (Tool)
            {
                case Mode.Sculpt: HandleSculpt(); break;
                case Mode.Flatten: HandleFlatten(); break;
                case Mode.Ramp: HandleRamp(); break;
                case Mode.Cell: HandleCellRaise(); break;
            }
        }

        void HandleModeKeys()
        {
            if (InputCompat.Tool1Pressed) SetTool(Mode.Sculpt);
            if (InputCompat.Tool2Pressed) SetTool(Mode.Flatten);
            if (InputCompat.Tool3Pressed) SetTool(Mode.Ramp);
            if (InputCompat.Tool4Pressed) SetTool(Mode.Cell);

            if (InputCompat.BrushUpPressed) Resize(1);
            if (InputCompat.BrushDownPressed) Resize(-1);

            if (InputCompat.RoundUpPressed)
                RoundingIndex = Mathf.Min(RoundingIndex + 1, RoundingChoices.Length - 1);
            if (InputCompat.RoundDownPressed)
                RoundingIndex = Mathf.Max(RoundingIndex - 1, 0);
        }

        void Resize(int delta)
        {
            if (Tool == Mode.Ramp)
                RampWidthIndex = Mathf.Clamp(RampWidthIndex + delta, 0, WidthChoices.Length - 1);
            else if (Tool == Mode.Sculpt || Tool == Mode.Cell)
                MaxStepIndex = Mathf.Clamp(MaxStepIndex + delta, 0, MaxStepChoices.Length - 1);
        }

        void SetTool(Mode mode)
        {
            if (Tool == mode) return;
            Tool = mode;
            _hasAnchor = false;
            StopRepeat();
        }

        void CycleStep()
        {
            float scroll = InputCompat.Scroll;
            if (Mathf.Abs(scroll) < 0.01f) return;

            StepIndex = Mathf.Clamp(StepIndex + (scroll > 0f ? 1 : -1), 0, StepChoices.Length - 1);
        }

        void UpdateTarget()
        {
            HasTarget = false;
            HasCell = false;
            if (PickCollider == null) return;

            Ray ray = Cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));

            RaycastHit hit;
            if (!PickCollider.Raycast(ray, out hit, MaxReach)) return;

            int cx, cz;
            if (Chunk.Grid.WorldToCell(hit.point, out cx, out cz))
            {
                HasCell = true;
                TargetCx = cx;
                TargetCz = cz;
            }

            int vx, vz;
            if (!Chunk.Grid.WorldToNearestVertex(hit.point, out vx, out vz)) return;

            HasTarget = true;
            TargetVx = vx;
            TargetVz = vz;
        }

        void HandleHistory()
        {
            if (InputCompat.UndoPressed) Chunk.Undo();
            if (InputCompat.RedoPressed) Chunk.Redo();
        }

        void LateUpdate()
        {
            if (Marker == null) return;

            bool show = HasTarget && Tool == Mode.Sculpt;
            Marker.gameObject.SetActive(show);
            if (show) Marker.position = Chunk.Grid.VertexWorld(TargetVx, TargetVz);
        }

        // ---- footprint --------------------------------------------------------

        /// <summary>Vertex rect the current tool would touch, for the marquee.</summary>
        public bool TryFootprint(out int vx0, out int vz0, out int w, out int h)
        {
            vx0 = vz0 = 0; w = h = 0;

            if (Tool == Mode.Flatten || Tool == Mode.Cell) return TryCellBlock(out vx0, out vz0, out w, out h);

            if (!HasTarget) return false;

            if (Tool == Mode.Sculpt)
            {
                vx0 = TargetVx; vz0 = TargetVz; w = 1; h = 1;
                return true;
            }

            int half = Mathf.Max(RampWidth / 2, 0);
            return Block(TargetVx, TargetVz, half * 2 + 1, out vx0, out vz0, out w, out h);
        }

        /// <summary>
        /// The single primal cell under the crosshair, as a vertex rect: one cell spans
        /// four corners. No block size and no centring question -- you level the cell you
        /// are looking at, and its corners are shared so adjacent pads knit together.
        /// </summary>
        public bool TryCellBlock(out int vx0, out int vz0, out int w, out int h)
        {
            vx0 = vz0 = 0; w = h = 0;
            if (!HasCell) return false;

            vx0 = TargetCx;
            vz0 = TargetCz;
            w = 2;
            h = 2;
            return true;
        }

        bool Block(int cx, int cz, int n, out int vx0, out int vz0, out int w, out int h)
        {
            HeightGrid g = Chunk.Grid;
            int r = n / 2;

            int x0 = Mathf.Clamp(cx - r, 0, g.VertsX - 1);
            int z0 = Mathf.Clamp(cz - r, 0, g.VertsZ - 1);
            int x1 = Mathf.Clamp(cx + r, 0, g.VertsX - 1);
            int z1 = Mathf.Clamp(cz + r, 0, g.VertsZ - 1);

            vx0 = x0; vz0 = z0;
            w = x1 - x0 + 1;
            h = z1 - z0 + 1;
            return w > 0 && h > 0;
        }

        // ---- sculpt -----------------------------------------------------------

        int ResolveDirection(out bool pressedThisFrame)
        {
            pressedThisFrame = true;

            if (InputCompat.LeftPressed) return InputCompat.Ctrl ? -1 : 1;
            if (InputCompat.RightPressed) return -1;

            pressedThisFrame = false;

            if (InputCompat.LeftHeld) return InputCompat.Ctrl ? -1 : 1;
            if (InputCompat.RightHeld) return -1;

            return 0;
        }

        void HandleSculpt()
        {
            bool pressed;
            int dir = ResolveDirection(out pressed);
            if (dir == 0) { StopRepeat(); return; }
            if (!ShouldFire(pressed, dir)) return;
            if (!HasTarget) return;

            // Rounding relaxes the ring, so the command needs the 3x3 around the peak.
            int vx0, vz0, w, h;
            int span = Rounding > 0f ? 3 : 1;
            if (!Block(TargetVx, TargetVz, span, out vx0, out vz0, out w, out h)) return;

            LastEditRejected = !Chunk.Execute(new VertexAdjustCommand(
                vx0, vz0, w, h, TargetVx, TargetVz, dir * StepUnits, MaxStepUnits, Rounding));
        }

        /// <summary>
        /// Raise or lower all four corners of the hovered cell together, so it rises flat
        /// instead of tipping. The natural companion to per-corner sculpt: shape the
        /// boundary corner by corner, or move a whole cell at once.
        /// </summary>
        void HandleCellRaise()
        {
            bool pressed;
            int dir = ResolveDirection(out pressed);
            if (dir == 0) { StopRepeat(); return; }
            if (!ShouldFire(pressed, dir)) return;

            int vx0, vz0, w, h;
            if (!TryCellBlock(out vx0, out vz0, out w, out h)) return;

            LastEditRejected = !Chunk.Execute(new AdjustCellCommand(
                vx0, vz0, w, h, dir * StepUnits, MaxStepUnits));
        }

        // ---- flatten ----------------------------------------------------------

        void HandleFlatten()
        {
            int vx0, vz0, w, h;

            bool pressed = InputCompat.LeftPressed;
            if (!pressed && !InputCompat.LeftHeld) { StopRepeat(); return; }
            if (!ShouldFire(pressed, 1)) return;
            if (!TryCellBlock(out vx0, out vz0, out w, out h)) return;

            LastEditRejected = !Chunk.Execute(
                new FlattenAreaCommand(vx0, vz0, w, h, FlattenTarget()));
        }

        /// <summary>
        /// Height to level to: the mean of the four corners of the cell you are looking
        /// at, read live every time.
        ///
        /// There is no held datum. One used to exist, captured on a right-click, and it
        /// then outlived whatever the player did next -- sculpting a cell and levelling it
        /// snapped it back to a height chosen minutes earlier, with a HUD readout that
        /// never moved because it had nothing to do with the cell under the crosshair.
        /// </summary>
        public ushort FlattenTarget()
        {
            if (!HasCell) return 0;

            HeightGrid g = Chunk.Grid;
            int total = 0;
            int corners = 0;

            // Mean of the four corners, not the lowest one.
            //
            // Cutting to the low corner only ever removes material, which reads as the
            // tool digging the cell out from under you. Levelling a slope by hand means
            // taking soil off the high side and packing it into the low side, and the
            // mean is the exact height where those balance: cut equals fill, so the cell
            // costs nothing net to level.
            for (int j = 0; j <= 1; j++)
                for (int i = 0; i <= 1; i++)
                {
                    int vx = TargetCx + i;
                    int vz = TargetCz + j;
                    if (!g.InBounds(vx, vz)) continue;

                    total += g.GetRaw(vx, vz);
                    corners++;
                }

            if (corners == 0) return 0;

            // Snapped to the step the player is working in. A mean of four heights lands
            // between steps far more often than not, and a cell levelled to 4.15 m is one
            // no number of 0.1 m clicks can ever bring a neighbour level with.
            return HeightGrid.SnapRaw(total / (float)corners * HeightGrid.MetresPerUnit, StepUnits);
        }

        public float FlattenTargetMetres { get { return HeightGrid.ToMetres(FlattenTarget()); } }

        // ---- ramp -------------------------------------------------------------

        public Vector3 AnchorWorld
        {
            get { return Chunk == null ? Vector3.zero : Chunk.Grid.VertexWorld(_anchorVx, _anchorVz); }
        }

        public Vector3 AimWorld
        {
            get
            {
                if (Chunk == null || !HasTarget) return Vector3.zero;
                return Chunk.Grid.VertexWorld(TargetVx, TargetVz);
            }
        }

        void HandleRamp()
        {
            if (InputCompat.RightPressed) { _hasAnchor = false; return; }
            if (!InputCompat.LeftPressed) return;      // press only; no auto-repeat
            if (!HasTarget) return;

            HeightGrid g = Chunk.Grid;

            if (!_hasAnchor)
            {
                _hasAnchor = true;
                _anchorVx = TargetVx;
                _anchorVz = TargetVz;
                _anchorRaw = g.GetRaw(TargetVx, TargetVz);
                return;
            }

            if (TargetVx == _anchorVx && TargetVz == _anchorVz) return;

            float halfWidth = RampWidth * 0.5f;
            int pad = Mathf.CeilToInt(halfWidth) + 1;

            int x0 = Mathf.Clamp(Mathf.Min(_anchorVx, TargetVx) - pad, 0, g.VertsX - 1);
            int z0 = Mathf.Clamp(Mathf.Min(_anchorVz, TargetVz) - pad, 0, g.VertsZ - 1);
            int x1 = Mathf.Clamp(Mathf.Max(_anchorVx, TargetVx) + pad, 0, g.VertsX - 1);
            int z1 = Mathf.Clamp(Mathf.Max(_anchorVz, TargetVz) + pad, 0, g.VertsZ - 1);

            LastEditRejected = !Chunk.Execute(new RampCommand(
                x0, z0, x1 - x0 + 1, z1 - z0 + 1,
                _anchorVx, _anchorVz, _anchorRaw,
                TargetVx, TargetVz, g.GetRaw(TargetVx, TargetVz),
                halfWidth));

            _hasAnchor = false;
        }

        // ---- repeat -----------------------------------------------------------

        bool ShouldFire(bool pressed, int dir)
        {
            if (pressed || dir != _activeDir)
            {
                _activeDir = dir;
                _repeatTimer = RepeatInterval * 2f;
                return true;
            }

            _repeatTimer -= Time.unscaledDeltaTime;
            if (_repeatTimer > 0f) return false;

            _repeatTimer = RepeatInterval;
            return true;
        }

        void StopRepeat()
        {
            _repeatTimer = 0f;
            _activeDir = 0;
        }
    }
}
