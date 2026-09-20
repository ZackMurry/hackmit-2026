using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Pulls the authored cast from the orchestrator's scenario pack (<c>GET /v1/npcs</c>)
/// and applies it to the spawned NPCs, matched by <c>npc_id</c> or one of its aliases:
/// each character's opening line becomes the NPC's greeting, spoken with the server's
/// pre-recorded audio (same voice as the live agent, no API call, no round trip).
/// Characters with no agent configured are flagged so a silent NPC is explained in the
/// console rather than discovered in play. Everything degrades to <c>npcs.json</c> when
/// the server is down.
/// </summary>
public class CastClient : MonoBehaviour
{
    public static CastClient Instance { get; private set; }

    public bool loadOnStart = true;
    [Tooltip("Use the server's opening lines (text + recorded audio) as greetings.")]
    public bool applyGreetings = true;

    /// <summary>Loaded cast, or null.</summary>
    public ConversationClient.Cast Current { get; private set; }
    public event Action<ConversationClient.Cast> Loaded;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    void Start()
    {
        if (NpcManager.Instance != null)
            NpcManager.Instance.Changed += Apply; // NPCs respawn when npcs.json changes
        if (loadOnStart)
            StartCoroutine(Load());
    }

    void OnDestroy()
    {
        if (NpcManager.Instance != null)
            NpcManager.Instance.Changed -= Apply;
        if (Instance == this)
            Instance = null;
    }

    public IEnumerator Load()
    {
        var client = ConversationClient.Instance;
        if (client == null || !client.IsOnline)
            yield break;

        yield return client.LoadCast(
            cast =>
            {
                Current = cast;
                Debug.Log($"CastClient: {cast.title} ({cast.scenario_id}), {cast.npcs.Length} character(s)");
                Apply();
                Loaded?.Invoke(cast);
            },
            error => Debug.LogWarning($"CastClient: cast not loaded ({error}); using npcs.json as-is."));
    }

    /// <summary>Find the cast member a scene NPC id refers to (by id or alias), or null.</summary>
    public ConversationClient.CastNpc Resolve(string npcId)
    {
        if (Current?.npcs == null || string.IsNullOrEmpty(npcId))
            return null;
        foreach (var c in Current.npcs)
        {
            if (c.npc_id == npcId)
                return c;
            if (c.aliases != null && Array.IndexOf(c.aliases, npcId) >= 0)
                return c;
        }
        return null;
    }

    void Apply()
    {
        if (Current?.npcs == null || NpcManager.Instance == null)
            return;
        var client = ConversationClient.Instance;

        foreach (var pair in NpcManager.Instance.Spawned)
        {
            var talk = pair.Value != null ? pair.Value.GetComponent<NpcConversation>() : null;
            if (talk == null)
                continue;
            var member = Resolve(talk.npcId);
            if (member == null)
            {
                Debug.LogWarning($"CastClient: NPC '{talk.npcId}' is not in the server's cast; it will get 'unknown npc' errors when spoken to.");
                continue;
            }
            if (!member.ready)
                Debug.LogWarning($"CastClient: '{member.npc_id}' has no agent configured on the server (AGENT_ID_{member.npc_id.ToUpperInvariant()}).");
            if (!applyGreetings)
                continue;

            if (!string.IsNullOrEmpty(member.greeting))
                talk.greeting = member.greeting;
            if (!string.IsNullOrEmpty(member.greeting_audio) && client != null)
            {
                var target = talk;
                StartCoroutine(client.DownloadClip(member.greeting_audio, clip =>
                {
                    if (target != null && clip != null)
                        target.greetingClip = clip;
                }));
            }
        }
    }
}
