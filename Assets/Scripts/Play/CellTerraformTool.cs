using UnityEngine;
using Terraform.Core;
using Terraform.View;

namespace Terraform.Play
{
    /// <summary>
    /// One target, one unit: the cell under the crosshair. Sculpt moves its centre point,
    /// flatten pins its corners to that point. No second grid, no parity, no ring.
    /// </summary>
    public sealed class CellTerraformTool : MonoBehaviour
    {
        public enum Mode { Sculpt, Flatten }

        /// <summary>Height change per click, in fixed-point units. 20 == 1 m.</summary>
        public static readonly int[] StepChoices = { 1, 2, 5, 10, 20 };   // 5cm .. 1m

        /// <summary>Max height difference to an orthogonal neighbour. 0 = unlimited.</summary>
        public static readonly int[] MaxStepChoices = { 20, 40, 60, 80, 0 };   // 1m .. 4m

        public CellChunkView Chunk;
        public Camera Cam;
        public CellMarquee Marquee;
        public Collider PickCollider;

        public float MaxReach = 5f;
        public int StepIndex = 1;        // 2 units = 10 cm, matched to the vertex model
        public int MaxStepIndex = 2;     // 60 units = 3 m
        public float RepeatInterval = 0.07f;

        public Mode Tool { get; private set; }

        public bool HasTarget { get; private set; }
        public int TargetCx { get; private set; }
        public int TargetCz { get; private set; }

        public bool LastEditRejected { get; private set; }
        public int ActiveDirection { get { return _activeDir; } }

        /// <summary>Height the next flatten would pin, which is simply the cell's own.</summary>
        public float FlattenTargetMetres
        {
            get { return HasTarget ? Chunk.Grid.GetMetres(TargetCx, TargetCz) : 0f; }
        }

        public int StepUnits { get { return StepChoices[Mathf.Clamp(StepIndex, 0, StepChoices.Length - 1)]; } }
        public float StepMetres { get { return StepUnits * CellGrid.MetresPerUnit; } }
        public int MaxStepUnits { get { return MaxStepChoices[Mathf.Clamp(MaxStepIndex, 0, MaxStepChoices.Length - 1)]; } }

        float _repeatTimer;
        int _activeDir;

        void Update()
        {
            if (Chunk == null || Chunk.Grid == null || Cam == null) return;
            if (PickCollider == null) PickCollider = Chunk.GetComponent<Collider>();

            HandleKeys();
            UpdateTarget();

            if (InputCompat.UndoPressed) Chunk.Undo();
            if (InputCompat.RedoPressed) Chunk.Redo();

            if (Cursor.lockState != CursorLockMode.Locked) { StopRepeat(); return; }

            if (Tool == Mode.Sculpt) HandleSculpt();
            else HandleFlatten();
        }

        void HandleKeys()
        {
            if (InputCompat.Tool1Pressed) SetTool(Mode.Sculpt);
            if (InputCompat.Tool2Pressed) SetTool(Mode.Flatten);

            float scroll = InputCompat.Scroll;
            if (Mathf.Abs(scroll) > 0.01f)
                StepIndex = Mathf.Clamp(StepIndex + (scroll > 0f ? 1 : -1), 0, StepChoices.Length - 1);

            if (InputCompat.BrushUpPressed) MaxStepIndex = Mathf.Min(MaxStepIndex + 1, MaxStepChoices.Length - 1);
            if (InputCompat.BrushDownPressed) MaxStepIndex = Mathf.Max(MaxStepIndex - 1, 0);

        }

        void SetTool(Mode mode)
        {
            if (Tool == mode) return;
            Tool = mode;
            StopRepeat();
        }

        void UpdateTarget()
        {
            HasTarget = false;
            if (PickCollider == null) return;

            Ray ray = Cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));

            RaycastHit hit;
            if (!PickCollider.Raycast(ray, out hit, MaxReach)) return;

            int cx, cz;
            if (!Chunk.Grid.WorldToCell(hit.point, out cx, out cz)) return;

            HasTarget = true;
            TargetCx = cx;
            TargetCz = cz;
        }

        void LateUpdate()
        {
            if (Marquee == null) return;

            if (HasTarget) Marquee.Show(TargetCx, TargetCz);
            else Marquee.Hide();
        }

        int ResolveDirection(out bool pressed)
        {
            pressed = true;
            if (InputCompat.LeftPressed) return InputCompat.Ctrl ? -1 : 1;
            if (InputCompat.RightPressed) return -1;

            pressed = false;
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

            LastEditRejected = !Chunk.Execute(
                new CellSculptCommand(TargetCx, TargetCz, dir * StepUnits, MaxStepUnits));
        }

        void HandleFlatten()
        {
            bool pressed = InputCompat.LeftPressed;
            if (!pressed && !InputCompat.LeftHeld) { StopRepeat(); return; }
            if (!ShouldFire(pressed, 1)) return;
            if (!HasTarget) return;

            LastEditRejected = !Chunk.Execute(
                new CellFlattenCommand(TargetCx, TargetCz, MaxStepUnits));
        }

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
