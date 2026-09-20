using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The talking part of an NPC: hold the talk key while in range to record the
/// mic, release to send the turn through <see cref="ConversationClient"/>, then
/// voice the NPC's audio through <see cref="NpcSpeaker"/> (lip-sync) and subtitle
/// both transcripts. Each NPC keeps its own server session so the character
/// remembers the conversation.
/// </summary>
[RequireComponent(typeof(NpcSpeaker), typeof(NpcInteractable))]
public class NpcConversation : MonoBehaviour
{
    [Header("Character")]
    [Tooltip("npc_id sent with every turn; must match an agent configured on the server (e.g. luis, maria).")]
    public string npcId = "";
    [Tooltip("Said the first time the player walks up. Empty = none. CastClient replaces it with the server's opening line, which the live character then speaks; this text is the caption and the offline fallback.")]
    public string greeting = "";

    [Header("Input")]
    public Key talkKey = Key.E;
    [Tooltip("Taps shorter than this are ignored.")]
    public float minHold = 0.25f;
    public int maxRecordSeconds = 20;
    public int sampleRate = 16000;
    [Tooltip("Recordings whose loudest sample is below this are treated as silence.")]
    public float silenceThreshold = 0.01f;

    [Header("Manners")]
    [Tooltip("Longest this NPC waits for another to finish talking before speaking anyway.")]
    public float maxWaitForFloor = 20f;

    [Header("HUD")]
    public int fontSize = 18;
    public float subtitleSeconds = 10f;

    /// <summary>Human-readable state for the interact prompt; empty when idle.</summary>
    public string Status { get; private set; } = "";
    public bool Busy { get; private set; }
    public bool IsRecording => recording != null;

    /// <summary>True when there is somewhere to send turns to (push-to-talk is enabled).</summary>
    public bool IsConfigured => ConversationClient.Instance != null;

    /// <summary>Client-generated UUID reused for every turn with this NPC (server-side memory).</summary>
    public string SessionId { get; private set; }

    public class Exchange
    {
        public string heard;   // what the learner said (server transcript)
        public string reply;   // NPC line (server transcript = caption)
        public float time;
    }

    /// <summary>Last completed exchange, for other HUDs / logging.</summary>
    public Exchange Last { get; private set; }
    public event Action<Exchange> Replied;
    /// <summary>Fired once, when the greeting has been said (see <see cref="NpcMove.TriggerGreet"/>).</summary>
    public event Action Greeted;
    /// <summary>Fired for each scene action the server reports with a reply (serve_order, show_bill, play_gesture).</summary>
    public event Action<ConversationClient.SceneAction> Acted;

    NpcSpeaker speaker;
    NpcInteractable interactable;
    AudioClip recording;
    float recordStart;
    bool greeted;
    GUIStyle boxStyle, heardStyle, replyStyle;

    void Awake()
    {
        speaker = GetComponent<NpcSpeaker>();
        interactable = GetComponent<NpcInteractable>();
        SessionId = Guid.NewGuid().ToString();
    }

    void OnDestroy()
    {
        var client = ConversationClient.Instance;
        if (client != null && client.isActiveAndEnabled)
            client.StartCoroutine(client.EndSession(SessionId));
    }

    void Update()
    {
        if (!greeted && interactable.PlayerInRange && !string.IsNullOrEmpty(greeting))
        {
            greeted = true;
            StartCoroutine(SayGreeting());
        }

        var keyboard = Keyboard.current;
        if (keyboard == null || !IsConfigured)
            return;

        if (recording == null)
        {
            if (interactable.PlayerInRange && !Busy && keyboard[talkKey].wasPressedThisFrame
                && Cursor.lockState == CursorLockMode.Locked)
                StartRecording();
            return;
        }

        if (keyboard[talkKey].wasReleasedThisFrame || Time.time - recordStart >= maxRecordSeconds)
            StopRecording();
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

        StartCoroutine(SendTurn(WavUtility.FromSamples(samples, channels, rate)));
    }

    /// <summary>Send one recorded turn to the server and voice/subtitle the reply. Usable from code with any WAV.</summary>
    public IEnumerator SendTurn(byte[] wav)
    {
        var client = ConversationClient.Instance;
        if (client == null)
            yield break;

        Busy = true;
        Status = "Thinking…";

        ConversationClient.Reply reply = null;
        string error = null;
        yield return client.Send(new ConversationClient.Turn { npcId = npcId, sessionId = SessionId, audioWav = wav },
                                 r => reply = r, e => error = e);
        if (error != null || reply == null)
        {
            Fail(error ?? "empty reply");
            yield break;
        }

        Last = new Exchange { heard = reply.heard, reply = reply.text, time = Time.time };
        Replied?.Invoke(Last);
        foreach (var action in reply.actions)
            Act(action);

        yield return Say(reply.text, reply.clip);

        Status = "";
        Busy = false;
    }

    /// <summary>
    /// Open the conversation and let the character say hello in their own voice. This
    /// also pays the connection cost now, so the player's first sentence is answered as
    /// fast as every later one. Offline, or if the server fails, the authored line is
    /// still "said" with the placeholder voice so the scene keeps moving.
    /// </summary>
    IEnumerator SayGreeting()
    {
        Busy = true;
        string text = greeting;
        AudioClip clip = null;
        var client = ConversationClient.Instance;
        if (client != null && client.IsOnline)
        {
            Status = "…";
            yield return client.Warm(SessionId, npcId, reply =>
            {
                if (reply == null)
                    return;
                clip = reply.clip;
                if (!string.IsNullOrEmpty(reply.text))
                    text = reply.text;
            }, error => Debug.LogWarning($"{name}: could not open the conversation for the greeting: {error}"));
        }
        Last = new Exchange { reply = text, time = Time.time };
        yield return Say(text, clip);
        Busy = false;
        Greeted?.Invoke();
    }

    /// <summary>Voice a line: the server's audio when it sent some, else the placeholder blah-blah.</summary>
    IEnumerator Say(string text, AudioClip clip = null)
    {
        if (string.IsNullOrEmpty(text) && clip == null)
            yield break;

        // Nobody talks over anybody: wait for whoever has the floor to finish. Luis
        // holds his greeting while Maria is still welcoming you, and a reply that
        // lands mid-sentence waits its turn.
        float waitStart = Time.time;
        while (NpcSpeaker.SomeoneElseSpeaking(speaker) && Time.time - waitStart < maxWaitForFloor)
        {
            var other = NpcSpeaker.Talking.GetComponent<NpcInteractable>();
            Status = other != null ? $"Waiting for {other.displayName} to finish…" : "Waiting…";
            yield return null;
        }

        Status = "Speaking…";
        if (clip != null)
            speaker.Speak(clip);
        else
            speaker.SpeakTest(Mathf.Clamp((text ?? "").Length / 12f, 1f, 8f));

        // Keep Status/Busy until she has finished so a new recording doesn't cut her off.
        yield return null;
        while (speaker.IsSpeaking)
            yield return null;
        if (clip != null)
            Destroy(clip);
        Status = "";
    }

    /// <summary>
    /// The server already answered the character, so this only mirrors the action in the
    /// world: quests tagged with it tick off, and NpcSchedule may start a move
    /// (<see cref="NpcMove.TriggerAction"/>). Gestures have no animations yet.
    /// </summary>
    void Act(ConversationClient.SceneAction action)
    {
        if (action == null || string.IsNullOrEmpty(action.action))
            return;
        string detail = action.action switch
        {
            "serve_order" or "show_bill" => $"{string.Join(", ", action.items ?? Array.Empty<string>())} = {action.total_mxn} MXN",
            "play_gesture" => action.gesture,
            _ => "",
        };
        Debug.Log($"{name}: action {action.action} {detail}");
        QuestManager.Instance?.CompleteByAction(action.action);
        Acted?.Invoke(action);
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

    void OnGUI()
    {
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
        }

        var lines = new List<(string, GUIStyle)>();
        if (!string.IsNullOrEmpty(Last.heard))
            lines.Add(($"You: {Last.heard}", heardStyle));
        if (!string.IsNullOrEmpty(Last.reply))
            lines.Add(($"{interactable.displayName}: {Last.reply}", replyStyle));
        if (lines.Count == 0)
            return;

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
