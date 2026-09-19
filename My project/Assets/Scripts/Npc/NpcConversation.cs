using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The talking part of an NPC: hold the talk key while in range to record the
/// mic (or press the type key for a text box), send the turn through
/// <see cref="ConversationClient"/> and show/voice what comes back.
///
/// The server does the understanding; this component only applies its reply:
/// the line is voiced through <see cref="NpcSpeaker"/> (lip-sync), quests go to
/// <see cref="QuestManager"/>, moves to <see cref="NpcSchedule"/>, and the
/// translation / correction / hint are drawn as IMGUI subtitles.
/// </summary>
[RequireComponent(typeof(NpcSpeaker), typeof(NpcInteractable))]
public class NpcConversation : MonoBehaviour
{
    [Header("Character")]
    [Tooltip("Sent with every turn so the server knows which character is talking.")]
    public string npcId = "";
    [Tooltip("ISO language code sent with every turn.")]
    public string languageCode = "es";
    [Tooltip("Said the first time the player walks up. Empty = none.")]
    public string greeting = "";

    [Header("Input")]
    public Key talkKey = Key.E;
    [Tooltip("Hold Tab to see the translation and a hint of what to say next.")]
    public Key helpKey = Key.Tab;
    [Tooltip("Type a line instead of speaking (no mic needed). Enter sends, Esc cancels.")]
    public Key typeKey = Key.T;
    [Tooltip("Taps shorter than this are ignored.")]
    public float minHold = 0.25f;
    public int maxRecordSeconds = 20;
    public int sampleRate = 16000;
    [Tooltip("Recordings whose loudest sample is below this are treated as silence.")]
    public float silenceThreshold = 0.01f;

    [Header("HUD")]
    public int fontSize = 18;
    public float subtitleSeconds = 10f;

    /// <summary>Human-readable state for the interact prompt; empty when idle.</summary>
    public string Status { get; private set; } = "";
    public bool Busy { get; private set; }
    public bool IsRecording => recording != null;

    /// <summary>True when there is somewhere to send turns to (push-to-talk is enabled).</summary>
    public bool IsConfigured => ConversationClient.Instance != null;

    /// <summary>True while the typed-input box is open (cursor is unlocked, player can't move).</summary>
    public bool IsTyping => typing;

    public class Exchange
    {
        public string heard;        // what the learner said (transcript)
        public string reply;        // NPC line in the target language
        public string translation;
        public string correction;
        public string hint;
        public float time;
    }

    /// <summary>Last completed exchange, for other HUDs / logging.</summary>
    public Exchange Last { get; private set; }
    public event Action<Exchange> Replied;

    NpcSpeaker speaker;
    NpcInteractable interactable;
    NpcSchedule schedule;
    AudioClip recording;
    float recordStart;
    bool greeted;
    bool typing;
    string typed = "";
    int typingStartedFrame;
    GUIStyle boxStyle, heardStyle, replyStyle, noteStyle;

    void Awake()
    {
        speaker = GetComponent<NpcSpeaker>();
        interactable = GetComponent<NpcInteractable>();
        schedule = GetComponent<NpcSchedule>();
    }

    void Update()
    {
        if (!greeted && interactable.PlayerInRange && !string.IsNullOrEmpty(greeting))
        {
            greeted = true;
            StartCoroutine(SayGreeting());
        }

        if (typing && Cursor.lockState == CursorLockMode.Locked)
        {
            // Clicking the text box makes FirstPersonController grab the cursor back; keep it free.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        var keyboard = Keyboard.current;
        if (keyboard == null || typing)
            return;

        if (!IsConfigured)
            return;

        if (interactable.PlayerInRange && !Busy && recording == null
            && keyboard[typeKey].wasPressedThisFrame && Cursor.lockState == CursorLockMode.Locked)
        {
            BeginTyping();
            return;
        }

        if (recording == null)
        {
            if (interactable.PlayerInRange && !Busy && keyboard[talkKey].wasPressedThisFrame)
                StartRecording();
            return;
        }

        if (keyboard[talkKey].wasReleasedThisFrame || Time.time - recordStart >= maxRecordSeconds)
            StopRecording();
    }

    void BeginTyping()
    {
        typing = true;
        typed = "";
        typingStartedFrame = Time.frameCount;
        Status = "Type your line, Enter to send";
        // FirstPersonController ignores input while the cursor is free and re-locks on click.
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void EndTyping(bool send)
    {
        typing = false;
        Status = "";
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        string line = typed.Trim();
        if (send && line.Length > 0)
            StartCoroutine(SendTurn(new ConversationClient.Turn { text = line }));
    }

    void StartRecording()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogWarning($"{name}: no microphone found.");
            SetStatus("No microphone", 2f);
            return;
        }
        recording = Microphone.Start(null, false, maxRecordSeconds, sampleRate);
        recordStart = Time.time;
        Status = "Listening… (release to send)";
        if (speaker.IsSpeaking)
            speaker.Stop();
    }

    void StopRecording()
    {
        int samplesRecorded = Microphone.GetPosition(null);
        Microphone.End(null);
        var clip = recording;
        recording = null;
        float held = Time.time - recordStart;

        if (held < minHold || samplesRecorded <= 0)
        {
            Status = "";
            Destroy(clip);
            return;
        }

        var samples = new float[samplesRecorded * clip.channels];
        clip.GetData(samples, 0);
        int channels = clip.channels;
        int rate = clip.frequency;
        Destroy(clip);

        if (WavUtility.Peak(samples) < silenceThreshold)
        {
            SetStatus("Didn't hear anything", 2f);
            return;
        }

        StartCoroutine(SendTurn(new ConversationClient.Turn { audioWav = WavUtility.FromSamples(samples, channels, rate) }));
    }

    /// <summary>Send one learner turn to the server and act on the reply. Usable from code without a mic.</summary>
    public IEnumerator SendTurn(ConversationClient.Turn turn)
    {
        var client = ConversationClient.Instance;
        if (client == null)
            yield break;

        Busy = true;
        Status = turn.audioWav != null ? "Understanding…" : "Thinking…";
        turn.npcId = npcId;
        turn.languageCode = languageCode;

        ConversationClient.Reply reply = null;
        string error = null;
        yield return client.Send(turn, r => reply = r, e => error = e);
        if (error != null || reply == null)
        {
            Fail(error ?? "empty reply");
            yield break;
        }

        var exchange = new Exchange
        {
            heard = string.IsNullOrEmpty(reply.heard) ? turn.text : reply.heard,
            reply = reply.text,
            translation = reply.translation,
            correction = reply.correction,
            hint = reply.hint,
            time = Time.time,
        };
        Last = exchange;
        Replied?.Invoke(exchange);

        if (reply.completedQuests != null && QuestManager.Instance != null)
            foreach (var id in reply.completedQuests)
                if (!string.IsNullOrEmpty(id))
                    QuestManager.Instance.Complete(id);

        yield return Say(reply.text, reply.clip);

        if (!string.IsNullOrEmpty(reply.move) && schedule != null)
            schedule.Trigger(reply.move);

        Status = "";
        Busy = false;
    }

    IEnumerator SayGreeting()
    {
        Busy = true;
        Last = new Exchange { reply = greeting, time = Time.time };
        yield return Say(greeting);
        Busy = false;
    }

    /// <summary>Voice a line: the server's audio when it sent some, else the placeholder blah-blah.</summary>
    IEnumerator Say(string text, AudioClip clip = null)
    {
        if (string.IsNullOrEmpty(text) && clip == null)
            yield break;

        Status = "Speaking…";
        if (clip != null)
            speaker.Speak(clip);
        else
            speaker.SpeakTest(Mathf.Clamp(text.Length / 12f, 1f, 8f));

        // Keep Status/Busy until she has finished so a new recording doesn't cut her off.
        yield return null;
        while (speaker.IsSpeaking)
            yield return null;
        if (clip != null)
            Destroy(clip);
        Status = "";
    }

    void Fail(string message)
    {
        Debug.LogWarning($"{name}: {message}");
        Busy = false;
        SetStatus(message.Length > 60 ? "Something went wrong (see console)" : message, 3f);
    }

    void SetStatus(string text, float seconds)
    {
        Status = text;
        StartCoroutine(ClearStatus(text, seconds));
    }

    IEnumerator ClearStatus(string text, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (Status == text)
            Status = "";
    }

    void DrawTypingBox()
    {
        var e = Event.current;
        // The key that opened the box arrives as an IMGUI event too; don't type it.
        if (e.type == EventType.KeyDown && Time.frameCount <= typingStartedFrame + 1)
        {
            e.Use();
            return;
        }
        if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
        {
            e.Use();
            EndTyping(send: true);
            return;
        }
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            e.Use();
            EndTyping(send: false);
            return;
        }

        float width = Mathf.Min(Screen.width * 0.6f, 800f);
        var rect = new Rect((Screen.width - width) / 2f, Screen.height * 0.45f, width, 70f);
        GUI.Box(rect, $"Say something to {interactable.displayName} (Enter to send, Esc to cancel)");
        var field = new Rect(rect.x + 12f, rect.y + 32f, width - 24f, 26f);
        GUI.SetNextControlName("npc-typed-line");
        typed = GUI.TextField(field, typed, 200);
        GUI.FocusControl("npc-typed-line");
    }

    void OnGUI()
    {
        if (typing)
            DrawTypingBox();

        if (Last == null || Cursor.lockState != CursorLockMode.Locked)
            return;
        bool recent = Time.time - Last.time < subtitleSeconds || speaker.IsSpeaking || Busy;
        if (!recent)
            return;

        if (boxStyle == null)
        {
            boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(16, 16, 10, 10) };
            replyStyle = new GUIStyle(GUI.skin.label) { fontSize = fontSize, wordWrap = true, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            replyStyle.normal.textColor = Color.white;
            heardStyle = new GUIStyle(replyStyle) { fontStyle = FontStyle.Normal, fontSize = fontSize - 3 };
            heardStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
            noteStyle = new GUIStyle(heardStyle) { fontStyle = FontStyle.Italic };
            noteStyle.normal.textColor = new Color(1f, 0.9f, 0.55f);
        }

        bool help = Keyboard.current != null && Keyboard.current[helpKey].isPressed;
        var lines = new List<(string, GUIStyle)>();
        if (!string.IsNullOrEmpty(Last.heard))
            lines.Add(($"You: {Last.heard}", heardStyle));
        if (!string.IsNullOrEmpty(Last.reply))
            lines.Add(($"{interactable.displayName}: {Last.reply}", replyStyle));
        if (help && !string.IsNullOrEmpty(Last.translation))
            lines.Add(($"“{Last.translation}”", heardStyle));
        if (!string.IsNullOrEmpty(Last.correction))
            lines.Add(($"Tip: {Last.correction}", noteStyle));
        if (help && !string.IsNullOrEmpty(Last.hint))
            lines.Add(($"Try: {Last.hint}", noteStyle));
        else if (!help && (!string.IsNullOrEmpty(Last.translation) || !string.IsNullOrEmpty(Last.hint)))
            lines.Add(($"hold {helpKey} for translation & hint", noteStyle));

        float width = Mathf.Min(Screen.width * 0.7f, 900f);
        float height = boxStyle.padding.vertical;
        foreach (var (text, style) in lines)
            height += style.CalcHeight(new GUIContent(text), width - boxStyle.padding.horizontal) + 2f;

        var rect = new Rect((Screen.width - width) / 2f, Screen.height - height - 24f, width, height);
        GUI.Box(rect, GUIContent.none, boxStyle);
        float y = rect.y + boxStyle.padding.top;
        foreach (var (text, style) in lines)
        {
            float h = style.CalcHeight(new GUIContent(text), width - boxStyle.padding.horizontal);
            GUI.Label(new Rect(rect.x + boxStyle.padding.left, y, width - boxStyle.padding.horizontal, h), text, style);
            y += h + 2f;
        }
    }
}
