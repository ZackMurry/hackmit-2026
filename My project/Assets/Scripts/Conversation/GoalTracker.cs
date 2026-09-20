using System.Collections;
using UnityEngine;

/// <summary>
/// Ticks the quest list from what the player actually said. The server's director
/// judges each turn a moment after the character has answered, so after every reply
/// this polls <c>GET /v1/runs/{run_id}/goals</c> until the review is in and completes
/// any quest whose id the director has ticked. Scene actions (<c>serve_order</c> →
/// the order quest) still tick through <see cref="QuestManager.CompleteByAction"/>;
/// the two agree and neither un-ticks. Offline, nothing happens and the HUD is what
/// <c>quests.json</c> says.
/// </summary>
public class GoalTracker : MonoBehaviour
{
    public static GoalTracker Instance { get; private set; }

    [Tooltip("Seconds after a reply before the first check; the director needs a moment.")]
    public float firstCheckDelay = 1.5f;
    [Tooltip("Seconds between checks while the server says it is still reviewing.")]
    public float recheckDelay = 2f;
    [Tooltip("Give up on a turn's review after this many checks; the next turn re-judges everything open anyway.")]
    public int maxChecks = 6;

    /// <summary>Last status the server sent, or null.</summary>
    public ConversationClient.GoalStatus Current { get; private set; }

    ConversationClient client;
    Coroutine polling;

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
        client = ConversationClient.Instance;
        if (client != null)
            client.Received += OnReply;
    }

    void OnDestroy()
    {
        if (client != null)
            client.Received -= OnReply;
        if (Instance == this)
            Instance = null;
    }

    void OnReply(ConversationClient.Reply reply)
    {
        // The reply carries the state as of the previous turn: apply it now, then
        // wait for this turn's review.
        Tick(reply.goalsAchieved, null);
        if (polling != null)
            StopCoroutine(polling);
        polling = StartCoroutine(Poll());
    }

    IEnumerator Poll()
    {
        yield return new WaitForSeconds(firstCheckDelay);
        for (int i = 0; i < maxChecks; i++)
        {
            bool reviewing = false, failed = false;
            yield return client.LoadGoals(
                status =>
                {
                    Current = status;
                    reviewing = status.reviewing;
                    foreach (var g in status.goals)
                        if (g.achieved)
                            Tick(new[] { g.id }, g.evidence_quote);
                },
                error =>
                {
                    // 503 = no director configured; say so and stop asking this turn.
                    Debug.LogWarning($"GoalTracker: goal status unavailable ({error}); quests tick from scene actions only.");
                    failed = true;
                });
            if (failed || !reviewing)
                break;
            yield return new WaitForSeconds(recheckDelay);
        }
        polling = null;
    }

    void Tick(string[] ids, string quote)
    {
        var quests = QuestManager.Instance;
        if (quests == null || ids == null)
            return;
        foreach (var id in ids)
        {
            var quest = quests.Find(id);
            if (quest == null || quest.IsDone)
                continue;
            quests.Complete(id);
            Debug.Log(string.IsNullOrEmpty(quote)
                ? $"GoalTracker: '{id}' done"
                : $"GoalTracker: '{id}' done — «{quote}»");
        }
    }
}
