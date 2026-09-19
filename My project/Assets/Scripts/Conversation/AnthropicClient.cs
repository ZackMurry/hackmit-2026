using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Thin coroutine wrapper over the Anthropic Messages API. One call, one
/// text reply; conversation memory is the caller's list of messages.
/// </summary>
public static class AnthropicClient
{
    const string Url = "https://api.anthropic.com/v1/messages";

    [Serializable]
    public class Message
    {
        public string role;
        public string content;

        public Message() { }

        public Message(string role, string content)
        {
            this.role = role;
            this.content = content;
        }
    }

    [Serializable]
    class Request
    {
        public string model;
        public int max_tokens;
        public string system;
        public Message[] messages;
    }

#pragma warning disable 0649 // DTO fields are filled by JsonUtility
    [Serializable]
    class Response
    {
        public string type;
        public ContentBlock[] content;
        public ApiError error;
    }

    [Serializable]
    class ContentBlock
    {
        public string type;
        public string text;
    }

    [Serializable]
    class ApiError
    {
        public string type;
        public string message;
    }
#pragma warning restore 0649

    /// <summary>
    /// Sends the system prompt and history; calls <paramref name="onDone"/>
    /// with the concatenated text blocks of the reply.
    /// </summary>
    public static IEnumerator Complete(string system, IList<Message> history, Action<string> onDone, Action<string> onError,
                                       int maxTokens = 400, string model = null)
    {
        var secrets = Secrets.Instance;
        if (!secrets.HasLlm)
        {
            onError?.Invoke("no Anthropic API key");
            yield break;
        }

        var body = new Request
        {
            model = string.IsNullOrEmpty(model) ? secrets.model : model,
            max_tokens = maxTokens,
            system = system,
            messages = new List<Message>(history).ToArray(),
        };

        using var req = new UnityWebRequest(Url, "POST")
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body))),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = 60,
        };
        req.SetRequestHeader("content-type", "application/json");
        req.SetRequestHeader("x-api-key", secrets.anthropicKey);
        req.SetRequestHeader("anthropic-version", "2023-06-01");

        yield return req.SendWebRequest();

        string text = req.downloadHandler.text;
        Response parsed = null;
        try { parsed = JsonUtility.FromJson<Response>(text); } catch { /* handled below */ }

        if (req.result != UnityWebRequest.Result.Success || parsed == null || parsed.type == "error")
        {
            string detail = parsed?.error?.message ?? (string.IsNullOrEmpty(text) ? req.error : Truncate(text, 300));
            onError?.Invoke($"Anthropic {req.responseCode}: {detail}");
            yield break;
        }

        var sb = new StringBuilder();
        if (parsed.content != null)
            foreach (var block in parsed.content)
                if (block.type == "text")
                    sb.Append(block.text);
        onDone?.Invoke(sb.ToString());
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
