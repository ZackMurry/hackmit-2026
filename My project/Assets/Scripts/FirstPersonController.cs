using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// First-person controller reading the new Input System devices directly.
/// No Input Actions asset, no PlayerInput component, no legacy Input Manager.
/// Attach to a GameObject with a CharacterController; the camera must be a
/// CHILD of it. Yaw goes on the body, pitch on the camera.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class FirstPersonController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Child camera transform. Auto-finds Camera.main if left empty.")]
    public Transform cameraTransform;

    [Header("Movement")]
    public float walkSpeed = 2.0f;
    public float runSpeed = 4.0f;
    public float gravity = -9.81f;

    [Header("Look")]
    [Tooltip("Degrees per pixel of mouse movement. Start around 0.1.")]
    public float mouseSensitivity = 0.1f;
    public float pitchMin = -85f;
    public float pitchMax = 85f;

    [Header("Debug")]
    public bool logInput = false;

    /// <summary>Cleared by <see cref="PlayerSeating"/> while sitting: look still works, WASD/gravity don't.</summary>
    public bool CanMove { get; set; } = true;

    CharacterController controller;
    float pitch;
    float verticalVelocity;

    void Awake()
    {
        controller = GetComponent<CharacterController>();

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        if (cameraTransform == null)
            Debug.LogError("FirstPersonController: no camera assigned and no Main Camera found.");
        else if (cameraTransform.parent != transform)
            Debug.LogWarning("FirstPersonController: camera should be a direct child of the player.");
    }

    void Start()
    {
        LockCursor(true);
    }

    void Update()
    {
        var keyboard = Keyboard.current;
        var mouse = Mouse.current;

        if (keyboard == null || mouse == null)
        {
            Debug.LogWarning("FirstPersonController: keyboard or mouse device not found.");
            return;
        }

        // Escape releases the cursor, click recaptures it.
        if (keyboard.escapeKey.wasPressedThisFrame)
            LockCursor(false);
        else if (mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
            LockCursor(true);

        if (Cursor.lockState != CursorLockMode.Locked)
            return;

        Look(mouse);
        if (CanMove)
            Move(keyboard);
    }

    void Look(Mouse mouse)
    {
        // delta is raw pixels moved this frame, not a normalised axis.
        Vector2 delta = mouse.delta.ReadValue();

        if (logInput)
            Debug.Log($"mouse delta = {delta}");

        float yaw = delta.x * mouseSensitivity;
        float pitchDelta = delta.y * mouseSensitivity;

        // Yaw on the body so movement follows the view.
        transform.Rotate(Vector3.up * yaw);

        // Pitch only on the camera, clamped.
        pitch = Mathf.Clamp(pitch - pitchDelta, pitchMin, pitchMax);
        cameraTransform.localEulerAngles = new Vector3(pitch, 0f, 0f);
    }

    void Move(Keyboard keyboard)
    {
        float x = 0f;
        float z = 0f;

        if (keyboard.aKey.isPressed) x -= 1f;
        if (keyboard.dKey.isPressed) x += 1f;
        if (keyboard.sKey.isPressed) z -= 1f;
        if (keyboard.wKey.isPressed) z += 1f;

        if (logInput && (x != 0f || z != 0f))
            Debug.Log($"move input = ({x}, {z})");

        Vector3 input = Vector3.ClampMagnitude(new Vector3(x, 0f, z), 1f);
        Vector3 direction = transform.TransformDirection(input);

        float speed = keyboard.leftShiftKey.isPressed ? runSpeed : walkSpeed;

        // Small downward bias keeps isGrounded reliable on uneven collider meshes.
        if (controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;

        verticalVelocity += gravity * Time.deltaTime;

        Vector3 velocity = direction * speed;
        velocity.y = verticalVelocity;

        controller.Move(velocity * Time.deltaTime);
    }

    void LockCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }
}
