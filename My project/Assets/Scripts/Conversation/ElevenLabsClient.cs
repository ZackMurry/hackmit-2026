using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// ElevenLabs speech-to-text (Scribe) and text-to-speech. TTS asks for raw PCM
/// so the clip can be built directly without an MP3 decoder.
/// </summary>
public static class ElevenLabsClient
{
    const string SttUrl = "https://api.elevenlabs.io/v1/speech-to-text";
    const string TtsUrl = "https://api.elevenlabs.io/v1/text-to-speech/";
    const int PcmRate = 24000;

    /// <summary>Default voice if npcs.json doesn't set one (multilingual models speak any language with any voice).</summary>
    public const string DefaultVoice = "EXAVITQu4vr4xnSDxMaL";

#pragma warning disable 0649 // DTO fields are filled by JsonUtility
    [Serializable]
    class SttResponse
    {
        public string language_code;
        public string text;
    }

    [Serializable]
    class TtsRequest
    {
        public string text;
        public string model_id;
        public string language_code;
    }

    [Serializable]
    class ErrorResponse
    {
        public Detail detail;
    }

    [Serializable]
    class Detail
    {
        public string status;
        public string message;
    }
#pragma warning restore 0649

    /// <summary>Transcribe a WAV; <paramref name="languageCode"/> like "es" biases recognition.</summary>
    public static IEnumerator Transcribe(byte[] wav, string languageCode, Action<string> onDone, Action<string> onError)
    {
        var secrets = Secrets.Instance;
        if (string.IsNullOrEmpty(secrets.elevenLabsKey))
        {
            onError?.Invoke("no ElevenLabs API key");
            yield break;
        }

        var form = new List<IMultipartFormSection>
        {
            new MultipartFormFileSection("file", wav, "speech.wav", "audio/wav"),
            new MultipartFormDataSection("model_id", "scribe_v1"),
            new MultipartFormDataSection("tag_audio_events", "false"),
        };
        if (!string.IsNullOrEmpty(languageCode))
            form.Add(new MultipartFormDataSection("language_code", languageCode));

        using var req = UnityWebRequest.Post(SttUrl, form);
        req.SetRequestHeader("xi-api-key", secrets.elevenLabsKey);
        req.timeout = 60;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            onError?.Invoke($"ElevenLabs STT {req.responseCode}: {ErrorMessage(req)}");
            yield break;
        }

        SttResponse parsed = null;
        try { parsed = JsonUtility.FromJson<SttResponse>(req.downloadHandler.text); } catch { }
        onDone?.Invoke(parsed?.text?.Trim() ?? "");
    }

    /// <summary>Synthesize speech; the clip is mono 24 kHz.</summary>
    public static IEnumerator Speak(string text, string voiceId, string languageCode, Action<AudioClip> onDone, Action<string> onError)
    {
        var secrets = Secrets.Instance;
        if (string.IsNullOrEmpty(secrets.elevenLabsKey))
        {
            onError?.Invoke("no ElevenLabs API key");
            yield break;
        }

        var body = new TtsRequest
        {
            text = text,
            model_id = string.IsNullOrEmpty(secrets.ttsModel) ? "eleven_flash_v2_5" : secrets.ttsModel,
            language_code = languageCode,
        };
        string json = JsonUtility.ToJson(body);
        // language_code is only accepted by some models; drop it when unset.
        if (string.IsNullOrEmpty(languageCode))
            json = json.Replace(",\"language_code\":\"\"", "");

        string url = TtsUrl + (string.IsNullOrEmpty(voiceId) ? DefaultVoice : voiceId) + $"?output_format=pcm_{PcmRate}";
        using var req = new UnityWebRequest(url, "POST")
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = 60,
        };
        req.SetRequestHeader("content-type", "application/json");
        req.SetRequestHeader("xi-api-key", secrets.elevenLabsKey);
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            onError?.Invoke($"ElevenLabs TTS {req.responseCode}: {ErrorMessage(req)}");
            yield break;
        }

        onDone?.Invoke(WavUtility.ClipFromPcm16(req.downloadHandler.data, PcmRate));
    }

    static string ErrorMessage(UnityWebRequest req)
    {
        string text = req.downloadHandler?.text;
        if (string.IsNullOrEmpty(text))
            return req.error;
        try
        {
            var err = JsonUtility.FromJson<ErrorResponse>(text);
            if (!string.IsNullOrEmpty(err?.detail?.message))
                return err.detail.message;
        }
        catch { }
        return text.Length > 300 ? text.Substring(0, 300) + "…" : text;
    }
}
