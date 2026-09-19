using UnityEngine;

/// <summary>
/// Makes the NPC feel alive: the body turns to face the player when they are
/// close, the head tracks the player's eyes within a cone, and the spine
/// breathes. Bone rotations are applied in LateUpdate, i.e. layered on top of
/// whatever <see cref="NpcIdleAnimation"/> posed this frame.
/// </summary>
[RequireComponent(typeof(NpcAvatarLoader))]
public class NpcLookAt : MonoBehaviour
{
    [Header("Body")]
    [Tooltip("Turn to face the player when they are within this distance.")]
    public float turnRange = 5f;
    public float turnSpeed = 3f;

    [Header("Head")]
    [Tooltip("Bone names on the avatar rig.")]
    public string headBone = "Head";
    public string spineBone = "Spine1";
    public float headMaxYaw = 60f;
    public float headMaxPitch = 30f;
    public float headSpeed = 6f;
    [Tooltip("Beyond this distance the head stops tracking and the idle animation owns it.")]
    public float lookRange = 8f;

    [Header("Idle")]
    [Tooltip("Extra breathing sway on the spine, degrees. Set to 0 when the idle clips already breathe enough.")]
    public float breathAmplitude = 1.5f;
    public float breathRate = 0.25f;

    NpcAvatarLoader loader;
    NpcIdleAnimation idle;
    Transform player;
    Transform head;
    Transform spine;
    Quaternion headRestOffset;
    Quaternion spineRest;
    Quaternion headCurrent;
    float lookWeight;

    void Awake()
    {
        loader = GetComponent<NpcAvatarLoader>();
        idle = GetComponent<NpcIdleAnimation>();
        loader.Loaded += OnAvatarLoaded;
    }

    void Start()
    {
        var fpc = FindFirstObjectByType<FirstPersonController>();
        if (fpc != null)
            player = fpc.cameraTransform != null ? fpc.cameraTransform : fpc.transform;
    }

    void OnAvatarLoaded(Transform avatar)
    {
        head = loader.FindBone(headBone);
        spine = loader.FindBone(spineBone);

        if (head != null)
        {
            // Rest-pose offset between "look straight ahead" and the bone's own axes.
            headRestOffset = Quaternion.Inverse(transform.rotation) * head.rotation;
            headCurrent = head.rotation;
        }
        else
            Debug.LogWarning($"NpcLookAt: bone '{headBone}' not found on avatar.");

        if (spine != null)
            spineRest = spine.localRotation;
    }

    void Update()
    {
        if (player == null)
            return;

        Vector3 toPlayer = player.position - transform.position;
        toPlayer.y = 0f;
        if (toPlayer.sqrMagnitude < 0.01f || toPlayer.magnitude > turnRange)
            return;

        var target = Quaternion.LookRotation(toPlayer);
        transform.rotation = Quaternion.Slerp(transform.rotation, target, turnSpeed * Time.deltaTime);
    }

    void LateUpdate()
    {
        bool animated = idle != null && idle.IsPlaying;

        if (spine != null)
        {
            float breath = Mathf.Sin(Time.time * breathRate * 2f * Mathf.PI) * breathAmplitude;
            // With mocap running the clip already posed the spine this frame; add to it.
            // Without it, build from the captured rest pose so the offset doesn't accumulate.
            var basePose = animated ? spine.localRotation : spineRest;
            spine.localRotation = basePose * Quaternion.Euler(breath, 0f, 0f);
        }

        if (head == null || player == null)
            return;

        // Direction to the player's eyes, clamped to a cone around the body's forward.
        Vector3 dir = player.position - head.position;
        Vector3 local = transform.InverseTransformDirection(dir.normalized);
        float yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -headMaxYaw, headMaxYaw);
        float pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg, -headMaxPitch, headMaxPitch);

        var look = transform.rotation * Quaternion.Euler(pitch, yaw, 0f);
        var target = look * headRestOffset;

        // Fade tracking out when the player is far, letting the idle clip move the head.
        float wantWeight = dir.magnitude <= lookRange ? 1f : 0f;
        lookWeight = Mathf.MoveTowards(lookWeight, wantWeight, Time.deltaTime * 2f);

        headCurrent = Quaternion.Slerp(headCurrent, target, headSpeed * Time.deltaTime);
        head.rotation = animated ? Quaternion.Slerp(head.rotation, headCurrent, lookWeight) : headCurrent;
    }
}
