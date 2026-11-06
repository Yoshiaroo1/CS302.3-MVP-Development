using UnityEngine;

namespace ForensicNeRF
{
    /// <summary>
    /// Orbit camera driven by the UI: orbit around a pivot, pan, zoom, focus on points
    /// and snap to axis views. All motion is smoothed.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class ViewerCamera : MonoBehaviour
    {
        [Tooltip("Degrees of rotation per pixel dragged.")]
        public float orbitSpeed = 0.3f;
        [Tooltip("How much each scroll step or zoom button press changes the distance.")]
        public float zoomStep = 0.15f;
        public float minDistance = 0.05f;
        public float maxDistance = 1000f;
        [Tooltip("Higher is snappier, lower is smoother.")]
        public float smoothing = 14f;

        public Camera Cam { get; private set; }
        public Vector3 Pivot => targetPivot;
        public float Distance => targetDistance;

        Vector3 pivot, targetPivot;
        float yaw, targetYaw, pitch, targetPitch, distance, targetDistance;

        void Awake()
        {
            Cam = GetComponent<Camera>();
            Vector3 e = transform.eulerAngles;
            yaw = targetYaw = e.y;
            pitch = targetPitch = e.x > 180f ? e.x - 360f : e.x;
            distance = targetDistance = 5f;
            pivot = targetPivot = transform.position + transform.forward * distance;
        }

        public void SetViewport(Rect normalizedRect)
        {
            if (Cam != null && normalizedRect.width > 0f && normalizedRect.height > 0f)
                Cam.rect = normalizedRect;
        }

        /// <summary>Rotate around the pivot. Delta is in UI pixels (y down).</summary>
        public void Orbit(Vector2 delta)
        {
            targetYaw += delta.x * orbitSpeed;
            targetPitch = Mathf.Clamp(targetPitch + delta.y * orbitSpeed, -89.5f, 89.5f);
        }

        /// <summary>Slide the view so the scene follows the cursor. Delta is in UI pixels.</summary>
        public void Pan(Vector2 delta, float viewportHeightPixels)
        {
            float worldPerPixel = 2f * targetDistance * Mathf.Tan(Cam.fieldOfView * 0.5f * Mathf.Deg2Rad)
                                  / Mathf.Max(1f, viewportHeightPixels);
            Quaternion rot = Quaternion.Euler(targetPitch, targetYaw, 0f);
            Vector3 right = rot * Vector3.right;
            Vector3 up = rot * Vector3.up;
            targetPivot += (-right * delta.x + up * delta.y) * worldPerPixel;
        }

        /// <summary>Positive steps move away, negative steps move closer.</summary>
        public void Dolly(float steps)
        {
            targetDistance = Mathf.Clamp(targetDistance * Mathf.Pow(1f + zoomStep, steps), minDistance, maxDistance);
        }

        /// <summary>Make a point the new orbit centre, keeping the current viewing angle.</summary>
        public void FocusOn(Vector3 worldPoint, float? newDistance = null)
        {
            targetPivot = worldPoint;
            if (newDistance.HasValue)
                targetDistance = Mathf.Clamp(newDistance.Value, minDistance, maxDistance);
        }

        /// <summary>Fit a box in view from the current angle.</summary>
        public void Frame(Bounds worldBounds)
        {
            float radius = Mathf.Max(worldBounds.extents.magnitude, 0.01f);
            float halfFov = Cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float aspect = Mathf.Max(0.1f, Cam.aspect);
            float halfFovMin = Mathf.Min(halfFov, Mathf.Atan(Mathf.Tan(halfFov) * aspect));
            targetPivot = worldBounds.center;
            targetDistance = Mathf.Clamp(radius / Mathf.Sin(halfFovMin) * 0.85f, minDistance, maxDistance);
            maxDistance = Mathf.Max(maxDistance, targetDistance * 4f);
        }

        /// <summary>Look at the pivot from the given world axis (e.g. Vector3.up is a top view).</summary>
        public void SnapToAxis(Vector3 axis)
        {
            Vector3 forward = -axis.normalized;
            if (Mathf.Abs(forward.y) > 0.99f)
            {
                targetPitch = forward.y < 0f ? 89.5f : -89.5f;
            }
            else
            {
                targetPitch = 0f;
                targetYaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            }
        }

        public void ResetAngles(float newYaw = 30f, float newPitch = 25f)
        {
            targetYaw = newYaw;
            targetPitch = newPitch;
        }

        void LateUpdate()
        {
            float k = 1f - Mathf.Exp(-smoothing * Time.unscaledDeltaTime);
            pivot = Vector3.Lerp(pivot, targetPivot, k);
            yaw = Mathf.LerpAngle(yaw, targetYaw, k);
            pitch = Mathf.Lerp(pitch, targetPitch, k);
            distance = Mathf.Lerp(distance, targetDistance, k);

            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            transform.SetPositionAndRotation(pivot - rot * Vector3.forward * distance, rot);
        }
    }
}
