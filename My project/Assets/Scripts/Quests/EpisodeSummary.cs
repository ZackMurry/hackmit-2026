using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// End-of-episode report card. The episode ends when every quest is done (as reported
/// through <see cref="QuestManager"/>, i.e. by the conversation server's scene actions)
/// or when the player presses <see cref="endKey"/>. The screen then grades each quest
/// A+…F on language performance. Grades come from <see cref="scoresFile"/> for now; the
/// scoring API isn't built yet, and this is the shape it should return (see scores.json).
/// Same IMGUI approach as <see cref="QuestHud"/>, no Canvas needed.
/// </summary>
[RequireComponent(typeof(QuestManager))]
public class EpisodeSummary : MonoBehaviour
{
    public static EpisodeSummary Instance { get; private set; }

    [Tooltip("JSON file under StreamingAssets with the per-quest grades (stand-in for the scoring API).")]
    public string scoresFile = "scores.json";
    [Tooltip("End the episode as soon as all quests are done.")]
    public bool endWhenQuestsDone = true;
    [Tooltip("Ends the episode early (debug / walk away).")]
    public Key endKey = Key.Q;
    [Tooltip("Restarts the scene from the report card.")]
    public Key restartKey = Key.R;

    [Header("Look")]
    public int fontSize = 16;
    public int titleFontSize = 30;
    public int gradeFontSize = 64;
    [Range(0f, 1f)] public float dimAlpha = 0.72f;
    public Color panelColor = new(0.08f, 0.08f, 0.1f, 0.96f);
    public Color gradeA = new(0.55f, 0.85f, 0.55f);
    public Color gradeB = new(0.65f, 0.85f, 0.75f);
    public Color gradeC = new(0.95f, 0.85f, 0.45f);
    public Color gradeD = new(0.95f, 0.6f, 0.35f);
    public Color gradeF = new(0.95f, 0.4f, 0.4f);

    /// <summary>True once the report card is up; gameplay input is released.</summary>
    public bool Ended { get; private set; }
    public ScoreSheet Scores { get; private set; }
    public event Action Finished;

    [Serializable]
    public class ScoreSheet
    {
        public string overall = "";   // empty = averaged from the quest grades
        public string summary = "";
        public QuestScore[] scores = Array.Empty<QuestScore>();
    }

    [Serializable]
    public class QuestScore
    {
        public string id;       // quest id
        public string grade;    // A+ … F
        public string comment;  // one line of feedback
    }

    QuestManager quests;
    QuestHud hud;
    bool sawTodo;
    GUIStyle titleStyle, subtitleStyle, rowStyle, commentStyle, gradeStyle, bigGradeStyle, footerStyle;

    string ScoresPath => Path.Combine(Application.streamingAssetsPath, scoresFile);

    void Awake()
    {
        Instance = this;
        quests = GetComponent<QuestManager>();
        hud = GetComponent<QuestHud>();
    }

    void Start()
    {
        quests.Changed += OnQuestsChanged;
        // Statuses persist in quests.json; a run that starts fully done is stale state
        // from the previous episode, not a finished one.
        if (AllDone())
        {
            Debug.Log("EpisodeSummary: quests were already complete at start; resetting for a fresh episode.");
            quests.ResetAll();
        }
        OnQuestsChanged();
    }

    void OnDestroy()
    {
        if (quests != null)
            quests.Changed -= OnQuestsChanged;
        if (Instance == this)
            Instance = null;
    }

    void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null)
            return;
        if (!Ended)
        {
            if (keyboard[endKey].wasPressedThisFrame && Cursor.lockState == CursorLockMode.Locked)
                End();
        }
        else if (keyboard[restartKey].wasPressedThisFrame)
            Restart();
    }

    bool AllDone()
    {
        var list = quests.Quests?.quests;
        if (list == null || list.Length == 0)
            return false;
        foreach (var q in list)
            if (!q.IsDone)
                return false;
        return true;
    }

    void OnQuestsChanged()
    {
        if (Ended)
            return;
        if (!AllDone())
        {
            sawTodo = true;
            return;
        }
        // Only finish on a transition we witnessed, never on stale persisted state.
        if (endWhenQuestsDone && sawTodo)
            End();
    }

    /// <summary>Stop the episode and show the report card.</summary>
    public void End()
    {
        if (Ended)
            return;
        Ended = true;
        Scores = LoadScores();

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        var player = FindFirstObjectByType<FirstPersonController>();
        if (player != null)
            player.enabled = false; // also stops a click from re-locking the cursor
        if (hud != null)
            hud.enabled = false;

        // Close every NPC session: agent time is billed while the socket is open.
        var client = ConversationClient.Instance;
        if (client != null)
            foreach (var talk in FindObjectsByType<NpcConversation>(FindObjectsSortMode.None))
                client.StartCoroutine(client.EndSession(talk.SessionId));

        Debug.Log($"EpisodeSummary: episode over, overall {OverallGrade()}");
        Finished?.Invoke();
    }

    void Restart()
    {
        quests.ResetAll();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        var scene = SceneManager.GetActiveScene();
#if UNITY_EDITOR
        // Scenes not listed in Build Settings (CancunCafe isn't) can still be reloaded in the editor this way.
        if (scene.buildIndex < 0)
        {
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(scene.path, new LoadSceneParameters(LoadSceneMode.Single));
            return;
        }
#endif
        SceneManager.LoadScene(scene.buildIndex);
    }

    ScoreSheet LoadScores()
    {
        if (!File.Exists(ScoresPath))
        {
            Debug.LogWarning($"EpisodeSummary: {ScoresPath} not found; no grades to show.");
            return new ScoreSheet();
        }
        try
        {
            return JsonUtility.FromJson<ScoreSheet>(File.ReadAllText(ScoresPath)) ?? new ScoreSheet();
        }
        catch (Exception e)
        {
            Debug.LogError($"EpisodeSummary: could not parse {ScoresPath}: {e.Message}");
            return new ScoreSheet();
        }
    }

    QuestScore ScoreFor(string questId)
    {
        if (Scores?.scores == null)
            return null;
        foreach (var s in Scores.scores)
            if (s.id == questId)
                return s;
        return null;
    }

    // ---- grades -----------------------------------------------------------------

    static readonly string[] Ladder = { "F", "D-", "D", "D+", "C-", "C", "C+", "B-", "B", "B+", "A-", "A", "A+" };

    /// <summary>Grade points on a 4.3 scale; -1 for anything unrecognised.</summary>
    static float Points(string grade)
    {
        int i = Array.IndexOf(Ladder, (grade ?? "").Trim().ToUpperInvariant());
        if (i < 0)
            return -1f;
        return i == 0 ? 0f : 0.7f + (i - 1) * 0.3f; // D- = 0.7 … A+ = 4.3
    }

    static string Letter(float points)
    {
        if (points <= 0.35f)
            return "F";
        int i = Mathf.Clamp(Mathf.RoundToInt((points - 0.7f) / 0.3f) + 1, 1, Ladder.Length - 1);
        return Ladder[i];
    }

    public string OverallGrade()
    {
        if (!string.IsNullOrEmpty(Scores?.overall))
            return Scores.overall.Trim().ToUpperInvariant();
        float sum = 0f;
        int n = 0;
        foreach (var q in quests.Quests.quests)
        {
            float p = Points(ScoreFor(q.id)?.grade);
            if (p < 0f)
                continue;
            sum += p;
            n++;
        }
        return n == 0 ? "—" : Letter(sum / n);
    }

    Color GradeColor(string grade)
    {
        char c = string.IsNullOrEmpty(grade) ? '?' : char.ToUpperInvariant(grade.Trim()[0]);
        return c switch
        {
            'A' => gradeA,
            'B' => gradeB,
            'C' => gradeC,
            'D' => gradeD,
            'F' => gradeF,
            _ => new Color(0.7f, 0.7f, 0.7f),
        };
    }

    // ---- drawing ----------------------------------------------------------------

    void OnGUI()
    {
        if (!Ended)
            return;
        EnsureStyles();

        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, dimAlpha);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);

        var list = quests.Quests?.quests ?? Array.Empty<Quest>();
        float width = Mathf.Min(Screen.width * 0.8f, 820f);
        float pad = 28f;
        float inner = width - pad * 2f;
        float gradeCol = 64f;
        float textCol = inner - gradeCol - 16f;

        // Measure.
        float rowH = rowStyle.CalcSize(new GUIContent("Ag")).y;
        float height = pad + titleStyle.CalcSize(new GUIContent("A")).y + 4f + bigGradeStyle.CalcSize(new GUIContent("A")).y + 12f;
        if (!string.IsNullOrEmpty(Scores?.summary))
            height += subtitleStyle.CalcHeight(new GUIContent(Scores.summary), inner) + 16f;
        var rows = new List<(Quest q, QuestScore s, float h)>();
        foreach (var q in list)
        {
            var s = ScoreFor(q.id);
            float h = rowH;
            if (!string.IsNullOrEmpty(s?.comment))
                h += commentStyle.CalcHeight(new GUIContent(s.comment), textCol) + 2f;
            rows.Add((q, s, h));
            height += h + 10f;
        }
        height += footerStyle.CalcSize(new GUIContent("A")).y + pad;

        var panel = new Rect((Screen.width - width) / 2f, Mathf.Max(24f, (Screen.height - height) / 2f), width, height);
        GUI.color = panelColor;
        GUI.DrawTexture(panel, Texture2D.whiteTexture);
        GUI.color = prev;

        float x = panel.x + pad;
        float y = panel.y + pad;

        string title = string.IsNullOrEmpty(quests.Quests?.title) ? "Episode complete" : quests.Quests.title;
        float titleH = titleStyle.CalcSize(new GUIContent(title)).y;
        GUI.Label(new Rect(x, y, inner, titleH), title, titleStyle);
        y += titleH + 4f;

        string overall = OverallGrade();
        float bigH = bigGradeStyle.CalcSize(new GUIContent(overall)).y;
        bigGradeStyle.normal.textColor = GradeColor(overall);
        GUI.Label(new Rect(x, y, inner, bigH), overall, bigGradeStyle);
        y += bigH + 12f;

        if (!string.IsNullOrEmpty(Scores?.summary))
        {
            float h = subtitleStyle.CalcHeight(new GUIContent(Scores.summary), inner);
            GUI.Label(new Rect(x, y, inner, h), Scores.summary, subtitleStyle);
            y += h + 16f;
        }

        foreach (var (q, s, h) in rows)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.08f);
            GUI.DrawTexture(new Rect(x, y - 4f, inner, h + 8f), Texture2D.whiteTexture);
            GUI.color = prev;

            string grade = string.IsNullOrEmpty(s?.grade) ? "—" : s.grade.Trim().ToUpperInvariant();
            gradeStyle.normal.textColor = GradeColor(grade);
            GUI.Label(new Rect(x, y, gradeCol, rowH), grade, gradeStyle);

            rowStyle.normal.textColor = q.IsDone ? Color.white : new Color(0.75f, 0.75f, 0.75f);
            string text = q.IsDone ? q.text : q.text + "  (not reached)";
            GUI.Label(new Rect(x + gradeCol + 16f, y, textCol, rowH), text, rowStyle);
            if (!string.IsNullOrEmpty(s?.comment))
            {
                float ch = commentStyle.CalcHeight(new GUIContent(s.comment), textCol);
                GUI.Label(new Rect(x + gradeCol + 16f, y + rowH + 2f, textCol, ch), s.comment, commentStyle);
            }
            y += h + 10f;
        }

        float footH = footerStyle.CalcSize(new GUIContent("A")).y;
        GUI.Label(new Rect(x, panel.yMax - pad - footH, inner, footH), $"Press {restartKey} to play again", footerStyle);
    }

    void EnsureStyles()
    {
        if (titleStyle != null)
            return;
        titleStyle = new GUIStyle(GUI.skin.label) { fontSize = titleFontSize, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        titleStyle.normal.textColor = Color.white;
        bigGradeStyle = new GUIStyle(titleStyle) { fontSize = gradeFontSize };
        subtitleStyle = new GUIStyle(GUI.skin.label) { fontSize = fontSize, wordWrap = true, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Italic };
        subtitleStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
        rowStyle = new GUIStyle(GUI.skin.label) { fontSize = fontSize, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        commentStyle = new GUIStyle(GUI.skin.label) { fontSize = fontSize - 3, wordWrap = true, alignment = TextAnchor.UpperLeft };
        commentStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
        gradeStyle = new GUIStyle(rowStyle) { fontSize = fontSize + 6, alignment = TextAnchor.MiddleCenter };
        footerStyle = new GUIStyle(GUI.skin.label) { fontSize = fontSize - 2, alignment = TextAnchor.MiddleCenter };
        footerStyle.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
    }
}
