using UnityEngine;

/// <summary>
/// Makes the NPC feel alive: while the player is engaging them (in talk range
/// facing them, or mid-conversation) a standing body turns to face the player
/// and the head tracks the player's eyes within a cone; while another NPC nearby
/// is talking, they watch that NPC instead; otherwise the NPC minds its own
/// business and the idle clip owns the pose. The spine always breathes.
/// Bone rotations are applied in LateUpdate, i.e. layered on top of whatever
/// <see cref="NpcIdleAnimation"/> posed this frame.
/// </summary>
[RequireComponent(typeof(NpcAvatarLoader))]
public class NpcLookAt : MonoBehaviour
{
    [Header("Attention")]
    [Tooltip("Seconds the NPC keeps looking at the player after the engagement ends.")]
    public float attentionHold = 3f;

    [Header("Body")]
    [Tooltip("Turn to face the player (while engaged) when they are within this distance.")]
    public float turnRange = 5f;
    public float turnSpeed = 3f;

    [Header("Head")]
    [Tooltip("Bone names on the avatar rig.")]
    public string headBone = "Head";
    public string spineBone = "Spine1";
    public float headMaxYaw = 60f;
    public float headMaxPitch = 30f;
    public float headSpeed = 6f;
    [Tooltip("Beyond this distance the head never tracks, even mid-engagement.")]
    public float lookRange = 8f;
    [Tooltip("Watch another NPC while they are talking (when the player is not engaging this one).")]
    public bool watchOthers = true;

    [Header("Idle")]
    [Tooltip("Extra breathing sway on the spine, degrees. Set to 0 when the idle clips already breathe enough.")]
    public float breathAmplitude = 1.5f;
    public float breathRate = 0.25f;

    NpcAvatarLoader loader;
    NpcIdleAnimation idle;
    NpcWalker walker;
    NpcSitter sitter;
    NpcInteractable interactable;
    NpcConversation talk;
    NpcSpeaker speaker;
    Transform player;
    Transform head;
    Transform spine;
    Quaternion headRestOffset;
    Quaternion spineRest;
    Quaternion headCurrent;
    float lookWeight;
    float attentionUntil = float.NegativeInfinity;
    // What we are looking at this frame: the player, or a talking NPC's head.
    Transform focus;
    NpcSpeaker watched;
    Transform watchedHead;

    /// <summary>True while the player has this NPC's attention (or for attentionHold after).</summary>
    public bool Attentive => Time.time < attentionUntil;

    void Awake()
    {
        loader = GetComponent<NpcAvatarLoader>();
        idle = GetComponent<NpcIdleAnimation>();
        walker = GetComponent<NpcWalker>();
        sitter = GetComponent<NpcSitter>();
        interactable = GetComponent<NpcInteractable>();
        talk = GetComponent<NpcConversation>();
        speaker = GetComponent<NpcSpeaker>();
        loader.Loaded += OnAvatarLoaded;
    }

    // Engaged = the player is in talk range looking at this NPC, or a turn with
    // them is in flight (recording, waiting on the server, or the NPC speaking).
    bool Engaged =>
        (interactable != null && interactable.PlayerInRange)
        || (talk != null && (talk.Busy || talk.IsRecording))
        || (speaker != null && speaker.IsSpeaking);

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

    /// <summary>The player when they are engaging us, else whoever else is talking nearby, else null.</summary>
    Transform PickFocus()
    {
        if (Engaged)
            return player;
        if (!watchOthers)
            return null;
        var talking = NpcSpeaker.Talking;
        if (talking == null || talking == speaker)
            return null;
        if (talking != watched)
        {
            watched = talking;
            var theirs = talking.GetComponent<NpcAvatarLoader>();
            watchedHead = theirs != null ? theirs.FindBone(headBone) : null;
        }
        var target = watchedHead != null ? watchedHead : talking.transform;
        return (target.position - transform.position).magnitude <= lookRange ? target : null;
    }

    void Update()
    {
        var wanted = PickFocus();
        if (wanted != null)
        {
            focus = wanted;
            attentionUntil = Time.time + attentionHold;
        }
        if (focus == null)
            focus = player;

        // Only an attentive, standing, non-walking NPC turns its body: the walker owns
        // the heading while walking, and a seated body stays put in its chair.
        if (focus == null || !Attentive
            || (walker != null && walker.OwnsHeading) || (sitter != null && sitter.IsSeated))
            return;

        Vector3 toFocus = focus.position - transform.position;
        toFocus.y = 0f;
        if (toFocus.sqrMagnitude < 0.01f || toFocus.magnitude > turnRange)
            return;

        var target = Quaternion.LookRotation(toFocus);
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

        if (head == null || focus == null)
            return;

        // Direction to the focus (the player's eyes, or the talker's head), clamped to
        // a cone around the body's forward.
        Vector3 dir = focus.position - head.position;
        Vector3 local = transform.InverseTransformDirection(dir.normalized);
        float yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -headMaxYaw, headMaxYaw);
        float pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg, -headMaxPitch, headMaxPitch);

        var look = transform.rotation * Quaternion.Euler(pitch, yaw, 0f);
        var target = look * headRestOffset;

        // Track only while something has our attention (and is near enough); otherwise
        // fade out and let the idle clip move the head.
        float wantWeight = Attentive && dir.magnitude <= lookRange ? 1f : 0f;
        lookWeight = Mathf.MoveTowards(lookWeight, wantWeight, Time.deltaTime * 2f);

        headCurrent = Quaternion.Slerp(headCurrent, target, headSpeed * Time.deltaTime);
        // Blend from whatever owns the head otherwise: the clip's pose, or the rest pose.
        var rest = animated ? head.rotation : transform.rotation * headRestOffset;
        head.rotation = Quaternion.Slerp(rest, headCurrent, lookWeight);
    }
}
