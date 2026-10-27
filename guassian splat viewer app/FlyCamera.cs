using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Free-fly camera for inspecting splats.
///   Hold right mouse  - look around
///   WASD              - move
///   Q / E             - down / up
///   Shift             - move faster
///   Scroll wheel      - change base speed
///   R                 - reset to start position
/// Works with both the new Input System and the legacy Input Manager.
/// </summary>
public class FlyCamera : MonoBehaviour
{
    public float speed = 2f;
    public float fastMultiplier = 4f;
    public float lookSensitivity = 0.15f;
    public float minSpeed = 0.1f, maxSpeed = 50f;

    /// <summary>Set by UI to stop the camera reacting while the user types or clicks in a panel.</summary>
    public static bool InputBlocked;

    float yaw, pitch;
    Vector3 startPos;
    Quaternion startRot;

    void Start()
    {
        startPos = transform.position;
        startRot = transform.rotation;
        SyncAngles();
    }

    void SyncAngles()
    {
        Vector3 e = transform.eulerAngles;
        yaw = e.y;
        pitch = e.x > 180f ? e.x - 360f : e.x;
    }

    public void ResetView()
    {
        transform.SetPositionAndRotation(startPos, startRot);
        SyncAngles();
    }

    void Update()
    {
        if (InputBlocked) return;

        bool looking;
        Vector2 mouseDelta;
        float scroll;
        Vector3 move = Vector3.zero;
        bool fast, reset;

#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        var kb = Keyboard.current;
        if (mouse == null || kb == null) return;

        looking = mouse.rightButton.isPressed;
        mouseDelta = mouse.delta.ReadValue();
        scroll = mouse.scroll.ReadValue().y / 120f;
        if (kb.wKey.isPressed) move.z += 1;
        if (kb.sKey.isPressed) move.z -= 1;
        if (kb.dKey.isPressed) move.x += 1;
        if (kb.aKey.isPressed) move.x -= 1;
        if (kb.eKey.isPressed) move.y += 1;
        if (kb.qKey.isPressed) move.y -= 1;
        fast = kb.leftShiftKey.isPressed;
        reset = kb.rKey.wasPressedThisFrame;
#else
        looking = Input.GetMouseButton(1);
        mouseDelta = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f;
        scroll = Input.mouseScrollDelta.y;
        if (Input.GetKey(KeyCode.W)) move.z += 1;
        if (Input.GetKey(KeyCode.S)) move.z -= 1;
        if (Input.GetKey(KeyCode.D)) move.x += 1;
        if (Input.GetKey(KeyCode.A)) move.x -= 1;
        if (Input.GetKey(KeyCode.E)) move.y += 1;
        if (Input.GetKey(KeyCode.Q)) move.y -= 1;
        fast = Input.GetKey(KeyCode.LeftShift);
        reset = Input.GetKeyDown(KeyCode.R);
#endif

        if (reset) { ResetView(); return; }

        if (looking)
        {
            yaw += mouseDelta.x * lookSensitivity;
            pitch -= mouseDelta.y * lookSensitivity;
            pitch = Mathf.Clamp(pitch, -89f, 89f);
            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }

        if (Mathf.Abs(scroll) > 0.01f)
            speed = Mathf.Clamp(speed * (1f + scroll * 0.1f), minSpeed, maxSpeed);

        float s = speed * (fast ? fastMultiplier : 1f);
        Vector3 worldMove = transform.right * move.x + transform.forward * move.z + Vector3.up * move.y;
        transform.position += worldMove * s * Time.deltaTime;
    }
}
