using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>OpenAI Whisper transcription, as an alternative STT when only an OpenAI key is at hand.</summary>
public static class OpenAiClient
{
    const string SttUrl = "https://api.openai.com/v1/audio/transcriptions";

#pragma warning disable 0649 // DTO fields are filled by JsonUtility
    [Serializable]
    class SttResponse
    {
        public string text;
    }
#pragma warning restore 0649

    public static IEnumerator Transcribe(byte[] wav, string languageCode, Action<string> onDone, Action<string> onError)
    {
        var secrets = Secrets.Instance;
        if (string.IsNullOrEmpty(secrets.openaiKey))
        {
            onError?.Invoke("no OpenAI API key");
            yield break;
        }

        var form = new List<IMultipartFormSection>
        {
            new MultipartFormFileSection("file", wav, "speech.wav", "audio/wav"),
            new MultipartFormDataSection("model", "whisper-1"),
        };
        if (!string.IsNullOrEmpty(languageCode))
            form.Add(new MultipartFormDataSection("language", languageCode));

        using var req = UnityWebRequest.Post(SttUrl, form);
        req.SetRequestHeader("Authorization", "Bearer " + secrets.openaiKey);
        req.timeout = 60;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            string text = req.downloadHandler?.text;
            onError?.Invoke($"OpenAI STT {req.responseCode}: {(string.IsNullOrEmpty(text) ? req.error : text)}");
            yield break;
        }

        SttResponse parsed = null;
        try { parsed = JsonUtility.FromJson<SttResponse>(req.downloadHandler.text); } catch { }
        onDone?.Invoke(parsed?.text?.Trim() ?? "");
    }
}
