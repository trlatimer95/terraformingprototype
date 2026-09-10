using UnityEngine;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

namespace Terraform.Play
{
    /// <summary>
    /// Thin shim over both Unity input backends so the prototype runs regardless of the
    /// project's Active Input Handling setting. Not a design statement, just friction
    /// removal: a spike that fails to run because of a project setting teaches nothing.
    /// </summary>
    public static class InputCompat
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER

        const float LookScale = 0.05f;   // new backend reports raw pixel delta

        public static Vector2 MousePosition
        {
            get { return Mouse.current == null ? Vector2.zero : Mouse.current.position.ReadValue(); }
        }

        public static bool LeftHeld
        {
            get { return Mouse.current != null && Mouse.current.leftButton.isPressed; }
        }

        public static bool LeftPressed
        {
            get { return Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame; }
        }

        public static bool RightHeld
        {
            get { return Mouse.current != null && Mouse.current.rightButton.isPressed; }
        }

        public static bool RightPressed
        {
            get { return Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame; }
        }

        public static float Scroll
        {
            get { return Mouse.current == null ? 0f : Mouse.current.scroll.ReadValue().y / 120f; }
        }

        public static Vector2 LookDelta
        {
            get { return Mouse.current == null ? Vector2.zero : Mouse.current.delta.ReadValue() * LookScale; }
        }

        public static bool Ctrl
        {
            get { return Keyboard.current != null && Keyboard.current.ctrlKey.isPressed; }
        }

        public static bool Shift
        {
            get { return Keyboard.current != null && Keyboard.current.shiftKey.isPressed; }
        }

        static bool Held(Key k) { return Keyboard.current != null && Keyboard.current[k].isPressed; }
        static bool Down(Key k) { return Keyboard.current != null && Keyboard.current[k].wasPressedThisFrame; }

        public static float MoveX { get { return Axis(Held(Key.D), Held(Key.A)); } }
        public static float MoveZ { get { return Axis(Held(Key.W), Held(Key.S)); } }
        public static float MoveY { get { return Axis(Held(Key.E), Held(Key.Q)); } }

        public static bool Jump { get { return Held(Key.Space); } }
        public static bool UndoPressed { get { return Down(Key.Z); } }
        public static bool RedoPressed { get { return Down(Key.Y); } }
        public static bool ToggleGridPressed { get { return Down(Key.Tab); } }
        public static bool EscapePressed { get { return Down(Key.Escape); } }
        public static bool ResetPressed { get { return Down(Key.R); } }
        public static bool ToggleModelPressed { get { return Down(Key.M); } }
        public static bool RoundDownPressed { get { return Down(Key.Comma); } }
        public static bool RoundUpPressed { get { return Down(Key.Period); } }
        public static bool Tool1Pressed { get { return Down(Key.Digit1); } }
        public static bool Tool2Pressed { get { return Down(Key.Digit2); } }
        public static bool Tool3Pressed { get { return Down(Key.Digit3); } }
        public static bool Tool4Pressed { get { return Down(Key.Digit4); } }
        public static bool BrushUpPressed { get { return Down(Key.Equals); } }
        public static bool BrushDownPressed { get { return Down(Key.Minus); } }
        public static bool ToggleSurfacePressed { get { return Down(Key.T); } }
        public static bool HolePressed { get { return Down(Key.H); } }
        public static bool PixelErrorUpPressed { get { return Down(Key.RightBracket); } }
        public static bool PixelErrorDownPressed { get { return Down(Key.LeftBracket); } }
        public static bool SampleWindowPressed { get { return Down(Key.N); } }
        public static bool SkyPressed { get { return Down(Key.L); } }
        public static bool SpanResolutionPressed { get { return Down(Key.C); } }
        public static bool SmoothPressed { get { return Down(Key.V); } }
        public static bool MarkersPressed { get { return Down(Key.G); } }
        public static bool SquarePressed { get { return Down(Key.K); } }
        public static bool DepositPressed { get { return Down(Key.O); } }
        public static bool PanelPressed { get { return Down(Key.F1); } }

#elif ENABLE_LEGACY_INPUT_MANAGER

        const float LookScale = 1f;      // legacy axes are already smoothed and scaled

        public static Vector2 MousePosition { get { return Input.mousePosition; } }
        public static bool LeftHeld { get { return Input.GetMouseButton(0); } }
        public static bool LeftPressed { get { return Input.GetMouseButtonDown(0); } }
        public static bool RightHeld { get { return Input.GetMouseButton(1); } }
        public static bool RightPressed { get { return Input.GetMouseButtonDown(1); } }
        public static float Scroll { get { return Input.mouseScrollDelta.y; } }

        public static Vector2 LookDelta
        {
            get
            {
                return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * LookScale;
            }
        }

        public static bool Ctrl
        {
            get { return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl); }
        }

        public static bool Shift
        {
            get { return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift); }
        }

        public static float MoveX { get { return Axis(Input.GetKey(KeyCode.D), Input.GetKey(KeyCode.A)); } }
        public static float MoveZ { get { return Axis(Input.GetKey(KeyCode.W), Input.GetKey(KeyCode.S)); } }
        public static float MoveY { get { return Axis(Input.GetKey(KeyCode.E), Input.GetKey(KeyCode.Q)); } }

        public static bool Jump { get { return Input.GetKey(KeyCode.Space); } }
        public static bool UndoPressed { get { return Input.GetKeyDown(KeyCode.Z); } }
        public static bool RedoPressed { get { return Input.GetKeyDown(KeyCode.Y); } }
        public static bool ToggleGridPressed { get { return Input.GetKeyDown(KeyCode.Tab); } }
        public static bool EscapePressed { get { return Input.GetKeyDown(KeyCode.Escape); } }
        public static bool ResetPressed { get { return Input.GetKeyDown(KeyCode.R); } }
        public static bool ToggleModelPressed { get { return Input.GetKeyDown(KeyCode.M); } }
        public static bool RoundDownPressed { get { return Input.GetKeyDown(KeyCode.Comma); } }
        public static bool RoundUpPressed { get { return Input.GetKeyDown(KeyCode.Period); } }
        public static bool Tool1Pressed { get { return Input.GetKeyDown(KeyCode.Alpha1); } }
        public static bool Tool2Pressed { get { return Input.GetKeyDown(KeyCode.Alpha2); } }
        public static bool Tool3Pressed { get { return Input.GetKeyDown(KeyCode.Alpha3); } }
        public static bool Tool4Pressed { get { return Input.GetKeyDown(KeyCode.Alpha4); } }
        public static bool BrushUpPressed { get { return Input.GetKeyDown(KeyCode.Equals); } }
        public static bool BrushDownPressed { get { return Input.GetKeyDown(KeyCode.Minus); } }
        public static bool ToggleSurfacePressed { get { return Input.GetKeyDown(KeyCode.T); } }
        public static bool HolePressed { get { return Input.GetKeyDown(KeyCode.H); } }
        public static bool PixelErrorUpPressed { get { return Input.GetKeyDown(KeyCode.RightBracket); } }
        public static bool PixelErrorDownPressed { get { return Input.GetKeyDown(KeyCode.LeftBracket); } }
        public static bool SampleWindowPressed { get { return Input.GetKeyDown(KeyCode.N); } }
        public static bool SkyPressed { get { return Input.GetKeyDown(KeyCode.L); } }
        public static bool SpanResolutionPressed { get { return Input.GetKeyDown(KeyCode.C); } }
        public static bool SmoothPressed { get { return Input.GetKeyDown(KeyCode.V); } }
        public static bool MarkersPressed { get { return Input.GetKeyDown(KeyCode.G); } }
        public static bool SquarePressed { get { return Input.GetKeyDown(KeyCode.K); } }
        public static bool DepositPressed { get { return Input.GetKeyDown(KeyCode.O); } }
        public static bool PanelPressed { get { return Input.GetKeyDown(KeyCode.F1); } }

#else

        public static Vector2 MousePosition { get { return Vector2.zero; } }
        public static bool LeftHeld { get { return false; } }
        public static bool LeftPressed { get { return false; } }
        public static bool RightHeld { get { return false; } }
        public static bool RightPressed { get { return false; } }
        public static float Scroll { get { return 0f; } }
        public static Vector2 LookDelta { get { return Vector2.zero; } }
        public static bool Ctrl { get { return false; } }
        public static bool Shift { get { return false; } }
        public static float MoveX { get { return 0f; } }
        public static float MoveZ { get { return 0f; } }
        public static float MoveY { get { return 0f; } }
        public static bool Jump { get { return false; } }
        public static bool UndoPressed { get { return false; } }
        public static bool RedoPressed { get { return false; } }
        public static bool ToggleGridPressed { get { return false; } }
        public static bool EscapePressed { get { return false; } }
        public static bool ResetPressed { get { return false; } }
        public static bool ToggleModelPressed { get { return false; } }
        public static bool RoundDownPressed { get { return false; } }
        public static bool RoundUpPressed { get { return false; } }
        public static bool Tool1Pressed { get { return false; } }
        public static bool Tool2Pressed { get { return false; } }
        public static bool Tool3Pressed { get { return false; } }
        public static bool Tool4Pressed { get { return false; } }
        public static bool BrushUpPressed { get { return false; } }
        public static bool BrushDownPressed { get { return false; } }
        public static bool ToggleSurfacePressed { get { return false; } }
        public static bool HolePressed { get { return false; } }
        public static bool PixelErrorUpPressed { get { return false; } }
        public static bool PixelErrorDownPressed { get { return false; } }
        public static bool SampleWindowPressed { get { return false; } }
        public static bool SkyPressed { get { return false; } }
        public static bool SpanResolutionPressed { get { return false; } }
        public static bool SmoothPressed { get { return false; } }
        public static bool MarkersPressed { get { return false; } }
        public static bool SquarePressed { get { return false; } }
        public static bool DepositPressed { get { return false; } }
        public static bool PanelPressed { get { return false; } }

#endif

#if ENABLE_INPUT_SYSTEM || ENABLE_LEGACY_INPUT_MANAGER
        static float Axis(bool positive, bool negative)
        {
            return (positive ? 1f : 0f) - (negative ? 1f : 0f);
        }
#endif
    }
}
