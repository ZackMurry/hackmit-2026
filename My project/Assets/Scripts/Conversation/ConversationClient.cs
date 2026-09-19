using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Unity side of the Scenar.io orchestrator (<c>orchestrator/</c>, see docs/api.md).
/// One speech turn = POST the learner's WAV to <c>/v1/speech</c> and get back the
/// NPC's audio plus both transcripts. The server owns speech recognition, the
/// character and the voice; this client only records and displays.
///
/// Without a <see cref="serverUrl"/> it answers with canned lines so the
/// interaction and HUD can be exercised offline.
/// </summary>
public class ConversationClient : MonoBehaviour
{
    public static ConversationClient Instance { get; private set; }

    [Tooltip("Base URL of the orchestrator (python -m orchestrator). Empty = canned offline replies.")]
    public string serverUrl = "http://127.0.0.1:8765";
    [Tooltip("Saved scenario to give the NPCs as context; set automatically by ScenarioClient. Empty = none.")]
    public string scenarioId = "";
    [Tooltip("Provider calls are capped at 60 s server-side.")]
    public int timeoutSeconds = 75;

    /// <summary>One learner turn: a WAV recording for one NPC within one session.</summary>
    public class Turn
    {
        public string npcId;
        public string sessionId;   // client-generated UUID, reused for conversation memory
        public byte[] audioWav;    // 16-bit PCM WAV
    }

    /// <summary>What the client hands back to the NPC.</summary>
    public class Reply
    {
        public string heard = "";  // user_transcript (may be empty)
        public string text = "";   // agent_transcript: the caption (may be empty)
        public AudioClip clip;     // decoded NPC audio, or null
    }

#pragma warning disable 0649 // DTO fields are filled by JsonUtility
    [Serializable]
    class SpeechResponse
    {
        public string session_id;
        public string npc_id;
        public string audio_base64;
        public string media_type;
        public int sample_rate;
        public string user_transcript;
        public string agent_transcript;
    }

    [Serializable]
    class ErrorResponse
    {
        public string detail;
    }
#pragma warning restore 0649

    public bool IsOnline => !string.IsNullOrEmpty(serverUrl);
    string Base => serverUrl.TrimEnd('/');

    static readonly string[] CannedLines =
    {
        "Claro, ¿algo más?",
        "Muy bien. ¿Quieres azúcar?",
        "Ahorita te lo paso, joven.",
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
            onDone?.Invoke(new Reply { heard = "(voice, no server connected)", text = CannedLines[canned++ % CannedLines.Length] });
            yield break;
        }

        string url = $"{Base}/v1/speech?session_id={turn.sessionId}&npc_id={UnityWebRequest.EscapeURL(turn.npcId)}&response_format=json";
        if (!string.IsNullOrEmpty(scenarioId))
            url += "&scenario_id=" + UnityWebRequest.EscapeURL(scenarioId);

        using var req = new UnityWebRequest(url, "POST")
        {
            uploadHandler = new UploadHandlerRaw(turn.audioWav) { contentType = "audio/wav" },
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = timeoutSeconds,
        };
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            onError?.Invoke(Describe(req));
            yield break;
        }

        SpeechResponse parsed = null;
        try { parsed = JsonUtility.FromJson<SpeechResponse>(req.downloadHandler.text); } catch { }
        if (parsed == null)
        {
            onError?.Invoke("server sent malformed reply");
            yield break;
        }

        var reply = new Reply { heard = parsed.user_transcript ?? "", text = parsed.agent_transcript ?? "" };
        if (!string.IsNullOrEmpty(parsed.audio_base64))
        {
            byte[] bytes = null;
            try { bytes = Convert.FromBase64String(parsed.audio_base64); }
            catch (Exception e) { Debug.LogWarning($"ConversationClient: bad audio_base64: {e.Message}"); }
            if (bytes != null)
                yield return Decode(bytes, parsed.media_type, parsed.sample_rate, c => reply.clip = c);
        }
        onDone?.Invoke(reply);
    }

    /// <summary>Close the server-side session (frees the provider connection). Fire and forget.</summary>
    public IEnumerator EndSession(string sessionId)
    {
        if (!IsOnline || string.IsNullOrEmpty(sessionId))
            yield break;
        using var req = UnityWebRequest.Delete($"{Base}/v1/speech/sessions/{sessionId}");
        req.timeout = 10;
        yield return req.SendWebRequest();
    }

    /// <summary>Turn the server's audio into a clip: WAV and raw PCM in-process, other containers via Unity's decoder.</summary>
    static IEnumerator Decode(byte[] bytes, string mediaType, int sampleRate, Action<AudioClip> onDone)
    {
        mediaType = (mediaType ?? "").ToLowerInvariant();
        switch (mediaType)
        {
            case "audio/wav":
            case "audio/x-wav":
                onDone(WavUtility.ClipFromWav(bytes, "npc-reply"));
                yield break;
            case "audio/pcm":
                onDone(WavUtility.ClipFromPcm16(bytes, sampleRate > 0 ? sampleRate : 16000, "npc-reply"));
                yield break;
        }

        // MP3 / OGG: Unity can only decode these from a URL, so bounce through a temp file.
        AudioType type = mediaType switch
        {
            "audio/mpeg" or "audio/mp4" => AudioType.MPEG,
            "audio/ogg" or "audio/webm" => AudioType.OGGVORBIS,
            _ => AudioType.UNKNOWN,
        };
        string path = Path.Combine(Application.temporaryCachePath, "npc-reply" + (type == AudioType.MPEG ? ".mp3" : ".ogg"));
        try { File.WriteAllBytes(path, bytes); }
        catch (Exception e)
        {
            Debug.LogWarning($"ConversationClient: could not write temp audio: {e.Message}");
            onDone(null);
            yield break;
        }
        using var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, type);
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"ConversationClient: could not decode {mediaType}: {req.error}");
            onDone(null);
            yield break;
        }
        onDone(DownloadHandlerAudioClip.GetContent(req));
    }

    /// <summary>Human-readable failure, unwrapping FastAPI's {"detail": ...}.</summary>
    public static string Describe(UnityWebRequest req)
    {
        string body = req.downloadHandler?.text;
        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                var err = JsonUtility.FromJson<ErrorResponse>(body);
                if (!string.IsNullOrEmpty(err?.detail))
                    body = err.detail;
            }
            catch { }
        }
        if (string.IsNullOrEmpty(body))
            body = req.error ?? "unknown error";
        return req.responseCode switch
        {
            0 => $"server unreachable ({req.error})",
            503 => "server: speech not configured (" + body + ")",
            _ => $"server {req.responseCode}: {(body.Length > 200 ? body.Substring(0, 200) + "…" : body)}",
        };
    }
}
