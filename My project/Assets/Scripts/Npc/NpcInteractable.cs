using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Proximity interaction: when the player is close and roughly facing the NPC,
/// shows a prompt and fires <see cref="Interacted"/> on E. Hook the conversation
/// loop (mic -> STT -> LLM -> ElevenLabs -> NpcSpeaker.Speak) to that event.
/// </summary>
public class NpcInteractable : MonoBehaviour
{
    [Tooltip("Shown in the prompt, e.g. 'Press E to talk to Sofía'.")]
    public string displayName = "Sofía";
    public float interactRange = 2.5f;
    [Tooltip("Player must be looking within this many degrees of the NPC.")]
    public float facingAngle = 45f;
    public Key interactKey = Key.E;

    [Tooltip("Until a conversation backend is connected, E plays a test line so " +
             "you can see the lip-sync working.")]
    public bool testSpeechOnInteract = true;

    /// <summary>Fired when the player presses the interact key while in range.</summary>
    public event Action Interacted;

    public bool PlayerInRange { get; private set; }

    Transform playerEyes;
    NpcSpeaker speaker;
    NpcConversation conversation;
    GUIStyle promptStyle;

    void Start()
    {
        speaker = GetComponent<NpcSpeaker>();
        conversation = GetComponent<NpcConversation>();

        var fpc = FindFirstObjectByType<FirstPersonController>();
        if (fpc != null)
            playerEyes = fpc.cameraTransform != null ? fpc.cameraTransform : fpc.transform;
    }

    void Update()
    {
        PlayerInRange = CheckInRange();
        if (!PlayerInRange)
            return;

        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard[interactKey].wasPressedThisFrame)
        {
            Interacted?.Invoke();
            // With a real conversation backend the key is push-to-talk instead.
            bool live = conversation != null && conversation.IsConfigured;
            if (testSpeechOnInteract && !live && speaker != null && !speaker.IsSpeaking)
                speaker.SpeakTest();
        }
    }

    bool CheckInRange()
    {
        if (playerEyes == null)
            return false;

        // Aim at chest height rather than the feet.
        Vector3 target = transform.position + Vector3.up * 1.4f;
        Vector3 toNpc = target - playerEyes.position;
        if (toNpc.magnitude > interactRange)
            return false;

        return Vector3.Angle(playerEyes.forward, toNpc) <= facingAngle;
    }

    void OnGUI()
    {
        if (!PlayerInRange || Cursor.lockState != CursorLockMode.Locked)
            return;

        promptStyle ??= new GUIStyle(GUI.skin.box)
        {
            fontSize = 20,
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(16, 16, 10, 10),
        };

        string text;
        if (conversation != null && !string.IsNullOrEmpty(conversation.Status))
            text = conversation.Status;
        else if (speaker != null && speaker.IsSpeaking)
            text = $"{displayName} is speaking…";
        else if (conversation != null && conversation.IsConfigured)
            text = $"Hold {interactKey} to talk to {displayName}";
        else
            text = $"Press {interactKey} to talk to {displayName}";

        var size = promptStyle.CalcSize(new GUIContent(text));
        var rect = new Rect((Screen.width - size.x) / 2f, Screen.height * 0.8f, size.x, size.y);
        GUI.Box(rect, text, promptStyle);
    }
}
