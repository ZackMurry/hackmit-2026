using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Loads (or generates) a Scenar.io scenario from the orchestrator's
/// <c>/v1/scenarios</c> endpoint and applies it to the scene: goals become the
/// quest list, characters rename the matching NPCs and give them their opening
/// line, and the scenario id is attached to every speech turn as context.
///
/// Set <see cref="scenarioId"/> to reuse a saved scenario, or leave it empty and
/// set <see cref="prompt"/> to generate a fresh one when the scene starts. The
/// id of the last generated scenario is remembered in PlayerPrefs so restarting
/// the scene doesn't cost another generation.
/// </summary>
public class ScenarioClient : MonoBehaviour
{
    public static ScenarioClient Instance { get; private set; }

    [Tooltip("Saved scenario UUID to load. Empty = generate from prompt (or reuse the last generated one).")]
    public string scenarioId = "";
    [Tooltip("Reuse the last generated scenario id (stored in PlayerPrefs) instead of generating again.")]
    public bool reuseLastGenerated = true;

    [Header("Generation")]
    public bool loadOnStart = true;
    [TextArea(2, 4)]
    public string prompt = "Order at the counter of a small café in Cancún, then sit and talk with your date";
    public string language = "Mexican Spanish";
    [Tooltip("CEFR level: A1, A2, B1, B2, C1 or C2.")]
    public string level = "A2";
    public int timeoutSeconds = 90;

    [Header("Apply")]
    [Tooltip("Replace quests.json with the scenario's goals.")]
    public bool applyGoals = true;
    [Tooltip("Rename NPCs whose id matches a character and use its opening_line as greeting.")]
    public bool applyCharacters = true;

    /// <summary>Loaded scenario, or null.</summary>
    public ScenarioResponse Current { get; private set; }
    public string Status { get; private set; } = "";
    public event Action<ScenarioResponse> Loaded;

    const string LastIdPref = "scenario.lastGeneratedId";

#pragma warning disable 0649 // DTO fields are filled by JsonUtility
    [Serializable]
    class ScenarioRequest
    {
        public string prompt;
        public string language;
        public string level;
    }

    [Serializable]
    public class Character
    {
        public string id;
        public string name;
        public string role;
        public string instructions;
        public string opening_line;
    }

    [Serializable]
    public class Goal
    {
        public string id;
        public string description;
        public string evidence_required;
        public string npc_id;
        public bool core;
    }

    [Serializable]
    public class Scenario
    {
        public string title;
        public string setting;
        public string language;
        public string level;
        public Character[] characters = Array.Empty<Character>();
        public Goal[] goals = Array.Empty<Goal>();
    }

    [Serializable]
    public class ScenarioResponse
    {
        public string scenario_id;
        public Scenario scenario;
        public string response;   // learner-facing introduction (text only)
    }
#pragma warning restore 0649

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
        if (loadOnStart)
            StartCoroutine(Load());
        if (NpcManager.Instance != null)
            NpcManager.Instance.Changed += ApplyCharacters; // NPCs respawn when npcs.json changes
    }

    void OnDestroy()
    {
        if (NpcManager.Instance != null)
            NpcManager.Instance.Changed -= ApplyCharacters;
    }

    /// <summary>Fetch the configured scenario (saved id, remembered id, or generate) and apply it.</summary>
    public IEnumerator Load()
    {
        var client = ConversationClient.Instance;
        if (client == null || !client.IsOnline)
        {
            Debug.Log("ScenarioClient: no server; keeping npcs.json / quests.json as they are.");
            yield break;
        }
        string root = client.serverUrl.TrimEnd('/');

        string id = scenarioId;
        if (string.IsNullOrEmpty(id) && reuseLastGenerated)
            id = PlayerPrefs.GetString(LastIdPref, "");

        if (!string.IsNullOrEmpty(id))
        {
            Status = "Loading scenario…";
            using var get = UnityWebRequest.Get($"{root}/v1/scenarios/{id}");
            get.timeout = 15;
            yield return get.SendWebRequest();
            if (get.result == UnityWebRequest.Result.Success && Apply(get.downloadHandler.text))
                yield break;
            if (get.responseCode == 404 && id != scenarioId)
                PlayerPrefs.DeleteKey(LastIdPref); // server lost it (different SCENARIO_DIR / machine)
            Debug.LogWarning($"ScenarioClient: could not load scenario {id}: {ConversationClient.Describe(get)}");
            if (!string.IsNullOrEmpty(scenarioId))
            {
                Status = "";
                yield break; // an explicit id that doesn't load shouldn't silently become a new scenario
            }
        }

        if (string.IsNullOrEmpty(prompt))
        {
            Status = "";
            yield break;
        }

        Status = "Generating scenario…";
        string body = JsonUtility.ToJson(new ScenarioRequest { prompt = prompt, language = language, level = level });
        using var post = new UnityWebRequest($"{root}/v1/scenarios", "POST")
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)) { contentType = "application/json" },
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = timeoutSeconds,
        };
        yield return post.SendWebRequest();
        if (post.result != UnityWebRequest.Result.Success || !Apply(post.downloadHandler.text))
        {
            Debug.LogWarning($"ScenarioClient: generation failed: {ConversationClient.Describe(post)}");
            Status = "";
            yield break;
        }
        PlayerPrefs.SetString(LastIdPref, Current.scenario_id);
        PlayerPrefs.Save();
    }

    bool Apply(string json)
    {
        ScenarioResponse parsed = null;
        try { parsed = JsonUtility.FromJson<ScenarioResponse>(json); } catch { }
        if (parsed?.scenario == null || string.IsNullOrEmpty(parsed.scenario_id))
            return false;

        Current = parsed;
        Status = "";
        if (ConversationClient.Instance != null)
            ConversationClient.Instance.scenarioId = parsed.scenario_id;
        Debug.Log($"ScenarioClient: \"{parsed.scenario.title}\" ({parsed.scenario_id}) — {parsed.response}");

        if (applyGoals && QuestManager.Instance != null)
        {
            var goals = parsed.scenario.goals ?? Array.Empty<Goal>();
            var quests = new Quest[goals.Length];
            for (int i = 0; i < goals.Length; i++)
                quests[i] = new Quest
                {
                    id = goals[i].id,
                    text = goals[i].core ? goals[i].description : goals[i].description + " (bonus)",
                    status = Quest.Todo,
                };
            QuestManager.Instance.Replace(new QuestList { title = parsed.scenario.title, quests = quests });
        }

        ApplyCharacters();
        Loaded?.Invoke(parsed);
        return true;
    }

    /// <summary>
    /// Bind scenario characters to spawned NPCs: by matching id, else by order in
    /// npcs.json. The NPC takes the character's id (speech turns are validated
    /// against the scenario's character ids), name and opening line.
    /// </summary>
    void ApplyCharacters()
    {
        if (!applyCharacters || Current?.scenario?.characters == null || NpcManager.Instance == null)
            return;

        var defs = NpcManager.Instance.Npcs?.npcs ?? Array.Empty<NpcDefinition>();
        var characters = Current.scenario.characters;
        for (int i = 0; i < characters.Length; i++)
        {
            var c = characters[i];
            var npc = NpcManager.Instance.Find(c.id);
            if (npc == null && i < defs.Length)
                npc = NpcManager.Instance.Find(defs[i].id);
            if (npc == null)
            {
                Debug.LogWarning($"ScenarioClient: no NPC for scenario character '{c.id}'.");
                continue;
            }

            var interactable = npc.GetComponent<NpcInteractable>();
            if (interactable != null && !string.IsNullOrEmpty(c.name))
                interactable.displayName = c.name;
            var talk = npc.GetComponent<NpcConversation>();
            if (talk != null)
            {
                talk.npcId = c.id;
                if (!string.IsNullOrEmpty(c.opening_line))
                    talk.greeting = c.opening_line;
            }
        }
    }

    void OnGUI()
    {
        if (string.IsNullOrEmpty(Status) || Cursor.lockState != CursorLockMode.Locked)
            return;
        var style = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
        GUI.Label(new Rect(0, 24f, Screen.width, 30f), Status, style);
    }
}
