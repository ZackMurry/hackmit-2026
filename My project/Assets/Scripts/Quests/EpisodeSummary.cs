using System;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// End of the episode: the bill. When every quest is done (as reported through
/// <see cref="QuestManager"/>, i.e. by the conversation server's scene actions) or the
/// player presses <see cref="endKey"/>, gameplay stops and Café Nader's till prints a
/// receipt — each quest is a line item and its "price" is the A+…F grade for how the
/// learner handled it in Spanish; the total is the overall grade, stamped on.
/// The grades are the server's verdict on everything said this run
/// (<c>POST /v1/grade</c> with the run id every turn was recorded under); the slip
/// prints its header at once and the lines arrive a few seconds later, like a till
/// waiting on the kitchen. Offline, <see cref="scoresFile"/> stands in with the same
/// shape. IMGUI like the rest of the HUD.
/// </summary>
[RequireComponent(typeof(QuestManager))]
public class EpisodeSummary : MonoBehaviour
{
    public static EpisodeSummary Instance { get; private set; }

    [Tooltip("Offline stand-in for POST /v1/grade: JSON under StreamingAssets in the same shape (grades per quest).")]
    public string scoresFile = "scores.json";
    [Tooltip("End the episode as soon as all quests are done.")]
    public bool endWhenQuestsDone = true;
    [Tooltip("Ends the episode early (debug / walk away).")]
    public Key endKey = Key.Q;
    [Tooltip("Restarts the scene from the receipt.")]
    public Key restartKey = Key.R;

    [Header("Receipt")]
    [Tooltip("Printed header lines. The first is the café's name.")]
    public string[] header = { "CAFÉ NADER", "Av. Nader 5 · Centro · Cancún, Q.R." };
    [Tooltip("NPC id whose displayName goes on the 'Te atendió' line. Empty = omit.")]
    public string waiterId = "maria";
    public string tableLabel = "MESA 2";
    [Tooltip("Slip width in design pixels; the whole receipt scales with screen height.")]
    public float width = 440f;
    [Tooltip("Slight tilt so it reads as paper dropped on the table, not a dialog.")]
    public float tiltDegrees = -1.5f;
    public int fontSize = 15;
    public Color paper = new(0.96f, 0.94f, 0.88f);
    public Color ink = new(0.17f, 0.16f, 0.15f);
    public Color faintInk = new(0.17f, 0.16f, 0.15f, 0.55f);
    [Tooltip("Rubber stamp for the total; also the ink for D and F lines.")]
    public Color stampRed = new(0.72f, 0.16f, 0.13f, 0.85f);
    [Range(0f, 1f)] public float dimAlpha = 0.6f;

    /// <summary>True once the receipt is up; gameplay input is released.</summary>
    public bool Ended { get; private set; }
    /// <summary>True while the server is still grading the run; the slip is half printed.</summary>
    public bool Grading { get; private set; }
    /// <summary>The verdict; null until <see cref="Grading"/> finishes.</summary>
    public ScoreSheet Scores { get; private set; }
    public event Action Finished;

    /// <summary>What <c>/v1/grade</c> returns and what scores.json holds; extra server fields are ignored.</summary>
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
        public string grade;    // A+ … F; empty = the moment never came up
        public string comment;  // one line of feedback
    }

    QuestManager quests;
    QuestHud hud;
    bool sawTodo;
    string printedAt;

    Font mono;
    Texture2D teeth;
    GUIStyle body, centred, small, smallCentred, bold, title, stamp;

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
        if (teeth != null)
            Destroy(teeth);
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

    /// <summary>Stop the episode and print the bill.</summary>
    public void End()
    {
        if (Ended)
            return;
        Ended = true;
        printedAt = DateTime.Now.ToString("dd/MM/yyyy  HH:mm");

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

        RequestGrades(client);
        Finished?.Invoke();
    }

    void RequestGrades(ConversationClient client)
    {
        if (client == null || !client.IsOnline)
        {
            Scores = LoadStandIn();
            Debug.Log($"EpisodeSummary: episode over (offline), stand-in grades, overall {OverallGrade()}");
            return;
        }
        if (client.TurnsSent == 0)
        {
            // The server has nothing recorded under this run; don't make it guess.
            Scores = new ScoreSheet { summary = "You didn't say a word to anyone. Next visit, try ordering something: even «un café, por favor» counts." };
            Debug.Log("EpisodeSummary: episode over, no turns to grade");
            return;
        }
        Grading = true;
        client.StartCoroutine(client.Grade(
            json =>
            {
                Grading = false;
                Scores = Parse(json, "the server's grade") ?? new ScoreSheet { summary = "The till printed something unreadable; the grades are lost." };
                Debug.Log($"EpisodeSummary: graded run {client.RunId}, overall {OverallGrade()}");
            },
            error =>
            {
                Grading = false;
                Scores = new ScoreSheet { summary = $"The till couldn't grade this visit ({error})." };
                Debug.LogWarning($"EpisodeSummary: grading failed: {error}");
            }));
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

    /// <summary>scores.json, for playing without a server. It grades everything, so only the quests the player actually reached are kept.</summary>
    ScoreSheet LoadStandIn()
    {
        if (!File.Exists(ScoresPath))
        {
            Debug.LogWarning($"EpisodeSummary: {ScoresPath} not found; no grades to show.");
            return new ScoreSheet();
        }
        var sheet = Parse(File.ReadAllText(ScoresPath), ScoresPath) ?? new ScoreSheet();
        var reached = new System.Collections.Generic.List<QuestScore>();
        foreach (var s in sheet.scores ?? Array.Empty<QuestScore>())
        {
            var q = quests.Find(s.id);
            if (q != null && q.IsDone)
                reached.Add(s);
        }
        sheet.scores = reached.ToArray();
        return sheet;
    }

    static ScoreSheet Parse(string json, string source)
    {
        try
        {
            return JsonUtility.FromJson<ScoreSheet>(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"EpisodeSummary: could not parse {source}: {e.Message}");
            return null;
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

    /// <summary>Grade points, F = 0 and 0.3 a step up to A+ = 4.0 (the server averages on the same scale); -1 for anything unrecognised.</summary>
    static float Points(string grade)
    {
        int i = Array.IndexOf(Ladder, (grade ?? "").Trim().ToUpperInvariant());
        if (i < 0)
            return -1f;
        return i == 0 ? 0f : 0.7f + (i - 1) * 0.3f; // D- = 0.7 … A+ = 4.0
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
        if (Grading)
            return "";
        if (!string.IsNullOrEmpty(Scores?.overall))
            return Scores.overall.Trim().ToUpperInvariant();
        float sum = 0f;
        int n = 0;
        foreach (var q in quests.Quests?.quests ?? Array.Empty<Quest>())
        {
            float p = Points(ScoreFor(q.id)?.grade);
            if (p < 0f)
                continue;
            sum += p;
            n++;
        }
        return n == 0 ? "—" : Letter(sum / n);
    }

    static bool IsPoor(string grade) =>
        !string.IsNullOrEmpty(grade) && (grade[0] == 'D' || grade[0] == 'F');

    // ---- the receipt ------------------------------------------------------------

    const float Pad = 26f;
    const float ToothHeight = 8f;

    void OnGUI()
    {
        if (!Ended)
            return;
        EnsureStyles();

        var prevColor = GUI.color;
        var prevMatrix = GUI.matrix;

        GUI.color = new Color(0.05f, 0.03f, 0.02f, dimAlpha);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);

        // Lay the slip out in design units, then place it with one matrix: scale with
        // the screen, a touch of tilt, centred.
        float inner = width - Pad * 2f;
        float height = Measure(inner);
        float scale = Mathf.Clamp(Screen.height / (height + 120f), 0.6f, Mathf.Max(0.6f, Screen.height / 820f));
        var centre = new Vector2(Screen.width / 2f, Screen.height / 2f);
        GUI.matrix = Matrix4x4.TRS(centre, Quaternion.Euler(0f, 0f, tiltDegrees), Vector3.one * scale)
                   * Matrix4x4.Translate(new Vector3(-width / 2f, -height / 2f, 0f));

        // Paper: shadow, body, torn top and bottom.
        var slip = new Rect(0f, 0f, width, height);
        GUI.color = new Color(0f, 0f, 0f, 0.4f);
        GUI.DrawTexture(new Rect(slip.x + 6f, slip.y + 10f, slip.width, slip.height), Texture2D.whiteTexture);
        GUI.color = paper;
        GUI.DrawTexture(slip, Texture2D.whiteTexture);
        float tiles = width / teeth.width;
        GUI.DrawTextureWithTexCoords(new Rect(0f, -ToothHeight, width, ToothHeight), teeth, new Rect(0f, 1f, tiles, -1f));
        GUI.DrawTextureWithTexCoords(new Rect(0f, height, width, ToothHeight), teeth, new Rect(0f, 0f, tiles, 1f));

        GUI.color = prevColor;
        Print(inner, draw: true);

        GUI.matrix = prevMatrix;
        GUI.color = prevColor;
    }

    float Measure(float inner) => Print(inner, draw: false);

    /// <summary>Walks the receipt top to bottom; draws when asked, always returns the height used.</summary>
    float Print(float inner, bool draw)
    {
        float x = Pad;
        float y = Pad + 4f;
        float line = body.CalcSize(new GUIContent("M")).y;
        float ch = body.CalcSize(new GUIContent("M")).x; // monospace: one character
        float gradeCol = ch * 3f;

        void Text(string s, GUIStyle style, Color color, float w = -1f, float indent = 0f)
        {
            if (string.IsNullOrEmpty(s))
                return;
            float h = style.CalcHeight(new GUIContent(s), w < 0f ? inner - indent : w);
            if (draw)
            {
                style.normal.textColor = color;
                GUI.Label(new Rect(x + indent, y, w < 0f ? inner - indent : w, h), s, style);
            }
            y += h;
        }

        void Rule()
        {
            y += line * 0.35f;
            if (draw)
            {
                GUI.color = faintInk;
                // Dashes the way a thermal printer does them.
                for (float dx = 0f; dx < inner; dx += ch)
                    GUI.DrawTexture(new Rect(x + dx, y, ch * 0.55f, 1.5f), Texture2D.whiteTexture);
                GUI.color = Color.white;
            }
            y += line * 0.5f;
        }

        void TwoUp(string left, string right, GUIStyle style, Color color)
        {
            if (draw)
            {
                style.normal.textColor = color;
                style.alignment = TextAnchor.MiddleLeft;
                GUI.Label(new Rect(x, y, inner, line), left, style);
                style.alignment = TextAnchor.MiddleRight;
                GUI.Label(new Rect(x, y, inner, line), right, style);
                style.alignment = TextAnchor.MiddleLeft;
            }
            y += line;
        }

        // Header.
        for (int i = 0; i < header.Length; i++)
            Text(header[i], i == 0 ? title : smallCentred, i == 0 ? ink : faintInk);
        Rule();
        TwoUp(printedAt, tableLabel, body, ink);
        string waiter = WaiterName();
        if (!string.IsNullOrEmpty(waiter))
            Text($"Te atendió: {waiter}", body, ink);
        Rule();

        // Line items: quest text, dotted leader, grade in the price column. While the
        // server is still grading, the column is blank: the slip is still printing.
        var list = quests.Quests?.quests ?? Array.Empty<Quest>();
        float textCol = inner - gradeCol - ch;
        foreach (var q in list)
        {
            var s = ScoreFor(q.id);
            string grade = Grading ? "" : !string.IsNullOrEmpty(s?.grade) ? s.grade.Trim().ToUpperInvariant() : "—";
            var gradeInk = IsPoor(grade) ? stampRed : grade == "—" ? faintInk : ink;

            string label = q.text ?? q.id;
            float labelW = body.CalcSize(new GUIContent(label)).x;
            float h = body.CalcHeight(new GUIContent(label), textCol);
            if (draw)
            {
                body.normal.textColor = ink;
                GUI.Label(new Rect(x, y, textCol, h), label, body);
                if (labelW <= textCol - ch * 2f)
                {
                    int dots = Mathf.FloorToInt((textCol - labelW) / ch) - 1;
                    body.normal.textColor = faintInk;
                    GUI.Label(new Rect(x + labelW + ch * 0.5f, y, textCol - labelW, line), new string('.', Mathf.Max(0, dots)), body);
                }
                bold.normal.textColor = gradeInk;
                bold.alignment = TextAnchor.MiddleRight;
                GUI.Label(new Rect(x + inner - gradeCol, y + h - line, gradeCol, line), grade, bold);
                bold.alignment = TextAnchor.MiddleLeft;
            }
            y += h;

            string note = Grading ? null : !string.IsNullOrEmpty(s?.comment) ? s.comment : q.IsDone ? null : "no llegaste hasta aquí";
            Text(note, small, faintInk, indent: ch * 2f);
            y += line * 0.35f;
        }

        Rule();
        string overall = OverallGrade();
        float totalY = y;
        TwoUp("TOTAL", overall, bold, IsPoor(overall) ? stampRed : ink);
        Rule();

        if (Grading)
        {
            Text("· · · imprimiendo · · ·", smallCentred, faintInk);
            Rule();
        }
        else if (!string.IsNullOrEmpty(Scores?.summary))
        {
            Text(Scores.summary, small, ink);
            Rule();
        }

        y += line * 0.2f;
        Text("¡Gracias por tu visita!", centred, ink);
        Text($"Pulsa {restartKey} para otra ronda", smallCentred, faintInk);
        y += Pad;

        // The grade is the total; the stamp says whether the bill is settled.
        if (draw && !Grading)
            Stamp(IsPoor(overall) ? "PENDIENTE" : "PAGADO", new Vector2(x + inner * 0.5f, totalY + line * 0.5f));

        return y + ToothHeight;
    }

    /// <summary>A rubber stamp across the total: bordered box, tilted against the slip.</summary>
    void Stamp(string text, Vector2 centre)
    {
        var size = stamp.CalcSize(new GUIContent(text));
        var box = new Rect(centre.x - size.x / 2f - 12f, centre.y - size.y / 2f - 2f, size.x + 24f, size.y + 4f);
        var saved = GUI.matrix;
        GUI.matrix *= Matrix4x4.TRS(centre, Quaternion.Euler(0f, 0f, -7f), Vector3.one) * Matrix4x4.Translate(-centre);
        GUI.color = stampRed;
        const float b = 3f;
        GUI.DrawTexture(new Rect(box.x, box.y, box.width, b), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(box.x, box.yMax - b, box.width, b), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(box.x, box.y, b, box.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(box.xMax - b, box.y, b, box.height), Texture2D.whiteTexture);
        GUI.color = Color.white;
        stamp.normal.textColor = stampRed;
        GUI.Label(box, text, stamp);
        GUI.matrix = saved;
    }

    string WaiterName()
    {
        if (string.IsNullOrEmpty(waiterId) || NpcManager.Instance == null)
            return "";
        var npc = NpcManager.Instance.Find(waiterId);
        var who = npc != null ? npc.GetComponent<NpcInteractable>() : null;
        return who != null ? who.displayName : "";
    }

    void EnsureStyles()
    {
        if (body != null)
            return;
        mono = Font.CreateDynamicFontFromOSFont(
            new[] { "Courier New", "Liberation Mono", "DejaVu Sans Mono", "Menlo", "Consolas", "Nimbus Mono PS", "Courier" }, fontSize);
        if (mono == null)
            mono = GUI.skin.font;

        body = new GUIStyle(GUI.skin.label) { font = mono, fontSize = fontSize, wordWrap = true, alignment = TextAnchor.MiddleLeft, richText = false };
        body.padding = new RectOffset(0, 0, 0, 0);
        centred = new GUIStyle(body) { alignment = TextAnchor.MiddleCenter };
        small = new GUIStyle(body) { fontSize = fontSize - 2 };
        smallCentred = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter };
        bold = new GUIStyle(body) { fontStyle = FontStyle.Bold, wordWrap = false };
        title = new GUIStyle(bold) { fontSize = fontSize + 6, alignment = TextAnchor.MiddleCenter };
        stamp = new GUIStyle(bold) { fontSize = fontSize + 12, alignment = TextAnchor.MiddleCenter };

        // Sawtooth for the torn edges: one tooth per tile, tinted with GUI.color when drawn.
        const int w = 16, h = 8;
        teeth = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
        for (int px = 0; px < w; px++)
        {
            int depth = h - Mathf.Abs(px - w / 2); // tooth points down from the paper
            for (int py = 0; py < h; py++)
                teeth.SetPixel(px, py, (h - 1 - py) < depth ? Color.white : Color.clear);
        }
        teeth.Apply();
    }
}
