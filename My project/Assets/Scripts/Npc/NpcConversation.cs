using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The talking part of an NPC: hold the talk key while in range to record the
/// mic, release to send. Speech -> text (ElevenLabs Scribe / Whisper) -> Claude
/// in character -> ElevenLabs voice -> <see cref="NpcSpeaker"/> lip-sync.
///
/// Claude answers as JSON with the line in the target language, an English
/// translation, a correction of the learner's mistake, a hint for what to say
/// next, quests it judges completed and optionally a move to perform. Quests
/// go to <see cref="QuestManager"/>, moves to <see cref="NpcSchedule"/>, so a
/// successful "un café, por favor" can send the barista walking.
/// Subtitles are drawn with IMGUI like the rest of the HUD.
/// </summary>
[RequireComponent(typeof(NpcSpeaker), typeof(NpcInteractable))]
public class NpcConversation : MonoBehaviour
{
    [Header("Character")]
    [TextArea(3, 8)]
    public string persona = "a friendly barista at a small café";
    public string language = "Spanish";
    [Tooltip("ISO code passed to speech recognition / synthesis.")]
    public string languageCode = "es";
    public string learnerLevel = "beginner (A1-A2)";
    [Tooltip("ElevenLabs voice id. Empty = ElevenLabsClient.DefaultVoice.")]
    public string voiceId = "";
    [Tooltip("Said the first time the player walks up. Empty = none.")]
    public string greeting = "";

    [Header("Input")]
    public Key talkKey = Key.E;
    [Tooltip("Hold Tab to see the translation and a hint of what to say next.")]
    public Key helpKey = Key.Tab;
    [Tooltip("Type a line instead of speaking (no mic / STT needed). Enter sends, Esc cancels.")]
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

    /// <summary>True when the full loop can run (keys for STT and the LLM).</summary>
    public bool IsConfigured => Secrets.Instance.HasLlm && Secrets.Instance.HasStt;

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

    [Serializable]
    class LlmReply
    {
        public string reply = "";
        public string translation = "";
        public string correction = "";
        public string hint = "";
        public string[] completedQuests = Array.Empty<string>();
        public string move = "";
    }

    NpcSpeaker speaker;
    NpcInteractable interactable;
    NpcSchedule schedule;
    readonly List<AnthropicClient.Message> history = new();
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

        if (Secrets.Instance.HasLlm && interactable.PlayerInRange && !Busy && recording == null
            && keyboard[typeKey].wasPressedThisFrame && Cursor.lockState == CursorLockMode.Locked)
        {
            BeginTyping();
            return;
        }

        if (!IsConfigured)
            return;

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
            StartCoroutine(Respond(line));
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

        StartCoroutine(Process(WavUtility.FromSamples(samples, channels, rate)));
    }

    IEnumerator Process(byte[] wav)
    {
        Busy = true;
        Status = "Understanding…";

        string heard = null, error = null;
        var stt = Secrets.Instance.sttProvider == "openai"
            ? OpenAiClient.Transcribe(wav, languageCode, t => heard = t, e => error = e)
            : ElevenLabsClient.Transcribe(wav, languageCode, t => heard = t, e => error = e);
        yield return stt;

        if (error != null || string.IsNullOrEmpty(heard))
        {
            Fail(error ?? "Didn't catch that");
            yield break;
        }

        Debug.Log($"{name} heard: {heard}");
        yield return Respond(heard);
    }

    /// <summary>Send a learner line (already text) through the brain and voice. Usable without a mic.</summary>
    public IEnumerator Respond(string heard)
    {
        Busy = true;
        Status = "Thinking…";

        history.Add(new AnthropicClient.Message("user", heard));
        TrimHistory();

        string raw = null, error = null;
        yield return AnthropicClient.Complete(BuildSystemPrompt(), history, r => raw = r, e => error = e);
        if (error != null)
        {
            history.RemoveAt(history.Count - 1);
            Fail(error);
            yield break;
        }

        var reply = ParseReply(raw);
        history.Add(new AnthropicClient.Message("assistant", raw));

        var exchange = new Exchange
        {
            heard = heard,
            reply = reply.reply,
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

        yield return Say(reply.reply);

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

    /// <summary>Voice a line through ElevenLabs, or the placeholder blah-blah when TTS isn't set up.</summary>
    IEnumerator Say(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        Status = "Speaking…";
        AudioClip clip = null;
        string error = null;
        if (Secrets.Instance.HasTts)
            yield return ElevenLabsClient.Speak(text, voiceId, languageCode, c => clip = c, e => error = e);
        if (error != null)
            Debug.LogWarning($"{name}: {error}; using placeholder voice.");

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

    void TrimHistory()
    {
        // Keep the last ~12 exchanges; drop pairs so it always starts with the user.
        while (history.Count > 24)
            history.RemoveRange(0, 2);
        while (history.Count > 0 && history[0].role != "user")
            history.RemoveAt(0);
    }

    string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        string npcName = interactable.displayName;
        sb.AppendLine($"You are {npcName}, {persona}. You are a character in an immersive {language}-learning game. " +
                      $"The player is a {language} learner at {learnerLevel} level who is talking to you out loud; " +
                      $"their words arrive via speech recognition, so tolerate small transcription errors.");
        sb.AppendLine($"Speak only {language} in \"reply\": one to three short, natural spoken sentences, no stage directions, no lists. " +
                      $"Stay in character and in the scene. Use simple vocabulary suited to the learner's level; if they struggle or " +
                      $"speak English, reply in slow, simple {language} and offer a way forward (e.g. a choice). Never break character to lecture.");
        if (greeted && !string.IsNullOrEmpty(greeting))
            sb.AppendLine($"You already greeted the learner with: \"{greeting}\"");

        var quests = QuestManager.Instance?.Quests?.quests;
        if (quests != null && quests.Length > 0)
        {
            sb.AppendLine("The learner has these goals. When the learner has genuinely achieved one through what they said " +
                          "(not merely mentioned it), include its id in \"completedQuests\":");
            foreach (var q in quests)
                sb.AppendLine($"- {q.id}: {q.text} [{(q.IsDone ? "done" : "todo")}]");
        }

        if (schedule != null && schedule.moves.Length > 0)
        {
            sb.AppendLine("You can perform one of these physical actions by putting its id in \"move\" (or \"\" for none). " +
                          "Only do so when it fits the conversation:");
            foreach (var m in schedule.moves)
                if (!string.IsNullOrEmpty(m.id))
                    sb.AppendLine($"- {m.id}" + (m.trigger == NpcMove.TriggerQuest ? $" (normally happens after quest {m.after})" : ""));
        }

        sb.AppendLine("Respond with ONLY a JSON object, no markdown fences, with exactly these keys: " +
                      $"\"reply\" (your line, in {language}), " +
                      "\"translation\" (English translation of reply), " +
                      $"\"correction\" (if the learner made a {language} mistake worth fixing: the corrected phrase in {language} " +
                      "plus a one-line English note; otherwise \"\"), " +
                      $"\"hint\" (one natural thing the learner could say next, in {language}), " +
                      "\"completedQuests\" (array of quest ids, usually []), " +
                      "\"move\" (action id or \"\").");
        return sb.ToString();
    }

    static LlmReply ParseReply(string raw)
    {
        string json = raw?.Trim() ?? "";
        int open = json.IndexOf('{');
        int close = json.LastIndexOf('}');
        if (open >= 0 && close > open)
            json = json.Substring(open, close - open + 1);

        try
        {
            var parsed = JsonUtility.FromJson<LlmReply>(json);
            if (parsed != null && !string.IsNullOrEmpty(parsed.reply))
                return parsed;
        }
        catch { /* fall through */ }

        // Not JSON: treat the whole thing as the spoken line so the conversation still flows.
        return new LlmReply { reply = raw ?? "" };
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
