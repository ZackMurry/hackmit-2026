using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// The one place the game talks to the conversation server. The client only
/// records/types the learner's line and displays what comes back; speech
/// recognition, the character brain and voice synthesis all live server-side.
///
/// Without a <see cref="serverUrl"/> it answers with canned lines so the
/// interaction and HUD can be exercised offline.
/// </summary>
public class ConversationClient : MonoBehaviour
{
    public static ConversationClient Instance { get; private set; }

    [Tooltip("Base URL of the conversation server, e.g. http://localhost:8000. Empty = canned offline replies.")]
    public string serverUrl = "";
    public int timeoutSeconds = 60;

    /// <summary>One learner turn: either a typed line or a WAV recording.</summary>
    public class Turn
    {
        public string npcId;
        public string languageCode;
        public string text;      // typed line, or null
        public byte[] audioWav;  // 16-bit PCM WAV, or null
    }

#pragma warning disable 0649 // DTO fields are filled by JsonUtility
    /// <summary>One timed caption segment; times are seconds from the start of the NPC's audio.</summary>
    [Serializable]
    public class Caption
    {
        public string text = "";
        public float start;
        public float end;
    }

    /// <summary>What the server returns for a turn. Everything but <c>text</c> is optional.</summary>
    [Serializable]
    public class Reply
    {
        public string heard = "";        // transcript of the learner (empty when the turn was typed)
        public string text = "";         // NPC line in the target language; the full caption
        public Caption[] captions = Array.Empty<Caption>(); // optional timed segments of text, shown in sync with audio
        public string translation = "";
        public string correction = "";
        public string hint = "";
        public string[] completedQuests = Array.Empty<string>();
        public string move = "";
        public string audio = "";        // base64 16-bit mono PCM of the NPC line, or empty
        public int audioSampleRate = 24000;

        [NonSerialized] public AudioClip clip; // decoded from audio by the client
    }
#pragma warning restore 0649

    public bool IsOnline => !string.IsNullOrEmpty(serverUrl);

    static readonly string[] CannedLines =
    {
        "Claro, ¿algo más?",
        "Muy bien. ¿Quieres azúcar?",
        "Perfecto, ahora mismo te lo preparo.",
        "¿De dónde eres?",
    };
    int canned;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        if (!IsOnline)
            Debug.Log("ConversationClient: no serverUrl set; NPCs will answer with canned lines.");
    }

    /// <summary>Send one turn; exactly one of <paramref name="onDone"/> / <paramref name="onError"/> is called.</summary>
    public IEnumerator Send(Turn turn, Action<Reply> onDone, Action<string> onError)
    {
        if (!IsOnline)
        {
            yield return new WaitForSeconds(0.6f);
            onDone?.Invoke(Canned(turn));
            yield break;
        }

        var form = new List<IMultipartFormSection>
        {
            new MultipartFormDataSection("npcId", turn.npcId ?? ""),
            new MultipartFormDataSection("language", turn.languageCode ?? ""),
        };
        if (turn.audioWav != null)
            form.Add(new MultipartFormFileSection("audio", turn.audioWav, "speech.wav", "audio/wav"));
        if (!string.IsNullOrEmpty(turn.text))
            form.Add(new MultipartFormDataSection("text", turn.text));

        using var req = UnityWebRequest.Post(serverUrl.TrimEnd('/') + "/turn", form);
        req.timeout = timeoutSeconds;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            string body = req.downloadHandler?.text;
            onError?.Invoke($"server {req.responseCode}: {(string.IsNullOrEmpty(body) ? req.error : Truncate(body, 200))}");
            yield break;
        }

        Reply reply = null;
        try { reply = JsonUtility.FromJson<Reply>(req.downloadHandler.text); } catch { }
        if (reply == null)
        {
            onError?.Invoke("server sent malformed reply");
            yield break;
        }

        if (!string.IsNullOrEmpty(reply.audio))
        {
            try { reply.clip = WavUtility.ClipFromPcm16(Convert.FromBase64String(reply.audio), reply.audioSampleRate); }
            catch (Exception e) { Debug.LogWarning($"ConversationClient: bad audio in reply: {e.Message}"); }
            reply.audio = ""; // don't keep the base64 around
        }
        onDone?.Invoke(reply);
    }

    Reply Canned(Turn turn)
    {
        string line = CannedLines[canned++ % CannedLines.Length];
        // Two timed segments over the placeholder voice's duration, so caption sync is visible offline.
        float seconds = Mathf.Clamp(line.Length / 12f, 1f, 8f);
        int split = line.IndexOf(' ', line.Length / 2);
        var captions = split < 0
            ? new[] { new Caption { text = line, start = 0f, end = seconds } }
            : new[]
            {
                new Caption { text = line.Substring(0, split), start = 0f, end = seconds / 2f },
                new Caption { text = line.Substring(split + 1), start = seconds / 2f, end = seconds },
            };
        return new Reply
        {
            heard = turn.text ?? "(voice, no server connected)",
            text = line,
            captions = captions,
            translation = "(offline canned reply)",
            hint = "Un café con leche, por favor.",
        };
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
