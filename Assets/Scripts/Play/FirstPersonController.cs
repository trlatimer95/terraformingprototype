using UnityEngine;

namespace Terraform.Play
{
    /// <summary>
    /// Minimal walking controller so terraforming is judged from eye level, where the
    /// player will actually experience it. A step looks fine from a flycam and reads
    /// completely differently when you are standing at the bottom of it.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class FirstPersonController : MonoBehaviour
    {
        public Transform Head;

        /// <summary>
        /// Terrain collider, referenced directly rather than raycast by layer. The
        /// controller's own capsule would otherwise intercept the ground probe below.
        /// </summary>
        public Collider TerrainCollider;

        public float WalkSpeed = 4.5f;
        public float SprintSpeed = 8.5f;
        public float JumpSpeed = 5f;
        public float Gravity = -20f;
        public float LookSensitivity = 3f;

        /// <summary>Largest rise the player is lifted over when ground is raised underfoot.</summary>
        public float MaxStandUpStep = 1.5f;

        CharacterController _cc;
        float _yaw;
        float _pitch;
        float _vy;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
        }

        /// <summary>
        /// Yaw is read on enable rather than in Awake so that disabling and re-enabling the
        /// controller around a teleport picks up the new facing. Read once at startup, a
        /// teleported player keeps looking whichever way they were pointing before.
        /// </summary>
        void OnEnable()
        {
            _yaw = transform.eulerAngles.y;
        }

        void Update()
        {
            if (Cursor.lockState == CursorLockMode.Locked) Look();
            Move();
            ResolveGroundRise();
        }

        void Look()
        {
            Vector2 look = InputCompat.LookDelta * LookSensitivity;

            _yaw += look.x;
            _pitch = Mathf.Clamp(_pitch - look.y, -89f, 89f);

            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
            if (Head != null) Head.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        void Move()
        {
            Vector3 wish = transform.right * InputCompat.MoveX + transform.forward * InputCompat.MoveZ;
            if (wish.sqrMagnitude > 1f) wish.Normalize();

            if (_cc.isGrounded)
            {
                if (_vy < 0f) _vy = -2f;                        // keep it pinned to slopes
                if (InputCompat.Jump) _vy = JumpSpeed;
            }

            _vy += Gravity * Time.deltaTime;

            float speed = InputCompat.Shift ? SprintSpeed : WalkSpeed;
            _cc.Move((wish * speed + Vector3.up * _vy) * Time.deltaTime);
        }

        /// <summary>
        /// Raising the ground under your own feet is the first thing anyone tries. The
        /// mesh moves but the capsule does not, so without this the controller ends up
        /// embedded in the terrain and jitters. Lift, never drop -- falling is gravity's job.
        /// </summary>
        void ResolveGroundRise()
        {
            if (TerrainCollider == null) return;

            float probeHeight = MaxStandUpStep + 0.5f;
            var ray = new Ray(transform.position + Vector3.up * probeHeight, Vector3.down);

            RaycastHit hit;
            if (!TerrainCollider.Raycast(ray, out hit, probeHeight + 0.5f)) return;

            float rise = hit.point.y - transform.position.y;
            if (rise <= 0.05f || rise > MaxStandUpStep) return;

            _cc.enabled = false;
            transform.position = new Vector3(transform.position.x, hit.point.y + 0.02f, transform.position.z);
            _cc.enabled = true;

            if (_vy < 0f) _vy = 0f;
        }
    }
}
