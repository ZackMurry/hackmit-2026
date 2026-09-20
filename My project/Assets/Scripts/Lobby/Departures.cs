using System;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The front door of Scenar.io: a boarding pass. The learner writes one line about
/// where they'd like to be and what they'd do there, taps how much of the language
/// they have, and presses Enter. The stub then flips through the stages a trip takes
/// — World Labs Marble building the place from the line, ElevenLabs giving the locals
/// voices, the director writing the goals — the pass is stamped, and the gate opens
/// onto the destination scene.
///
/// Every trip lands in <see cref="destinationScene"/> for now: Café Nader in Cancún is
/// the worked example of what a generated world looks like once you are inside it.
/// The line and level are kept in PlayerPrefs (<see cref="LastLine"/>,
/// <see cref="LastLevel"/>) for the day generation is wired up. IMGUI like the
/// receipt at the other end of the trip, and the same paper.
/// </summary>
public class Departures : MonoBehaviour
{
    [Header("Destination")]
    [Tooltip("Scene every trip lands in: the worked example of a generated world.")]
    public string destinationScene = "Assets/Scenes/CancunCafe.unity";

    [Header("Questions")]
    [Tooltip("Shown faintly in the empty line; also the line used if the learner boards without writing one.")]
    public string example = "a café in Cancún, ordering breakfast";
    public Level[] levels =
    {
        new() { label = "just starting", cefr = "A1" },
        new() { label = "getting by", cefr = "A2" },
        new() { label = "conversational", cefr = "B1" },
        new() { label = "nearly fluent", cefr = "B2" },
    };
    public int defaultLevel = 1;

    [Header("Boarding")]
    [Tooltip("How long each stage on the stub takes to flip over.")]
    public float secondsPerStage = 1.6f;
    public float fadeSeconds = 0.8f;
    public string gate = "2";
    [Tooltip("Where the learner ends up sitting; the receipt prints the same table.")]
    public string seat = "MESA 2";

    [Header("Look")]
    public Color backdrop = new(0.075f, 0.085f, 0.105f);
    public Color paper = new(0.96f, 0.94f, 0.88f);
    public Color ink = new(0.17f, 0.16f, 0.15f);
    public Color faintInk = new(0.17f, 0.16f, 0.15f, 0.55f);
    [Tooltip("Header bar, the board button and the stamp; the receipt's stamp red.")]
    public Color accent = new(0.72f, 0.16f, 0.13f);
    [Tooltip("Slight tilt so the pass reads as paper on the counter, not a dialog.")]
    public float tiltDegrees = -1f;

    [Serializable]
    public class Level
    {
        public string label;
        [Tooltip("CEFR level sent with the scenario request.")]
        public string cefr;
    }

    /// <summary>What the learner last boarded with; empty until they have.</summary>
    public static string LastLine => PlayerPrefs.GetString(LinePref, "");
    public static string LastLevel => PlayerPrefs.GetString(LevelPref, "");

    const string LinePref = "departures.line";
    const string LevelPref = "departures.level";

    /// <summary>The stub flips through these; the note prints under the pass.</summary>
    static readonly (string flap, string note)[] Stages =
    {
        ("SCOUTING", "Reading your line: working out where you are and who else is there."),
        ("BUILDING", "World Labs Marble is generating the place from your line."),
        ("CASTING", "ElevenLabs gives the locals their voices, and a reason to talk to you."),
        ("WRITING", "Turning what you want to practise into three small goals."),
        ("BOARDING", "The gate is open. Walk up to anyone and press E to talk."),
    };
    const int CastingStage = 2, BoardingStage = 4;

    enum Phase { Writing, Boarding, Departing }

    Phase phase = Phase.Writing;
    float phaseStart;
    string line = "";
    int level;
    bool loading;

    // Design units: everything is laid out for a 1280×720 canvas and scaled to fit.
    const float DesignW = 1280f, DesignH = 720f;
    const float PassW = 840f, PassH = 300f, StubW = 230f, HeaderH = 34f, Pad = 28f;
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    Font sans, mono;
    Texture2D disc;
    GUIStyle wordmark, tagline, label, field, placeholder, chip, fine, value, flap, button, note, stamp;

    string Line => string.IsNullOrWhiteSpace(line) ? example : line.Trim();
    string Cefr => levels.Length > 0 ? levels[Mathf.Clamp(level, 0, levels.Length - 1)].cefr : "";
    float Since => Time.time - phaseStart;
    int Stage => phase == Phase.Writing ? -1
               : phase == Phase.Departing ? Stages.Length - 1
               : Mathf.Min(Mathf.FloorToInt(Since / secondsPerStage), Stages.Length - 1);
    float StageSince => phase == Phase.Departing ? 99f : Since - Stage * secondsPerStage;

    void Start()
    {
        level = Mathf.Clamp(defaultLevel, 0, Mathf.Max(0, levels.Length - 1));
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void OnDestroy()
    {
        if (disc != null)
            Destroy(disc);
    }

    void Update()
    {
        if (phase == Phase.Boarding && Since >= Stages.Length * secondsPerStage)
        {
            phase = Phase.Departing;
            phaseStart = Time.time;
        }
        else if (phase == Phase.Departing && Since >= fadeSeconds)
            Load();
    }

    void Board()
    {
        if (phase != Phase.Writing)
            return;
        phase = Phase.Boarding;
        phaseStart = Time.time;
        GUI.FocusControl(null);
        PlayerPrefs.SetString(LinePref, Line);
        PlayerPrefs.SetString(LevelPref, Cefr);
        PlayerPrefs.Save();
        Debug.Log($"Departures: boarding for «{Line}» at {Cefr}");
    }

    void Load()
    {
        if (loading)
            return;
        loading = true;
        Debug.Log($"Departures: landing in {destinationScene}");
#if UNITY_EDITOR
        // The destination isn't in Build Settings; in the editor it can still be loaded by path.
        if (SceneUtility.GetBuildIndexByScenePath(destinationScene) < 0)
        {
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(destinationScene, new LoadSceneParameters(LoadSceneMode.Single));
            return;
        }
#endif
        SceneManager.LoadScene(destinationScene);
    }

    // ---- the desk -----------------------------------------------------------------

    void OnGUI()
    {
        EnsureStyles();
        HandleKeys();

        var prevMatrix = GUI.matrix;
        var prevColor = GUI.color;
        float scale = Mathf.Min(Screen.width / DesignW, Screen.height / DesignH);
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        float w = Screen.width / scale, h = Screen.height / scale;

        Fill(new Rect(0f, 0f, w, h), backdrop);
        Title(w);

        var pass = new Rect((w - PassW) / 2f, 250f, PassW, PassH);
        var centre = pass.center;
        var flat = GUI.matrix;
        GUI.matrix = flat * Matrix4x4.TRS(centre, Quaternion.Euler(0f, 0f, tiltDegrees), Vector3.one) * Matrix4x4.Translate(-centre);
        Pass(pass);
        GUI.matrix = flat;

        Footnote(w, pass.yMax + 34f);

        if (phase == Phase.Departing)
            Fill(new Rect(0f, 0f, w, h), new Color(0f, 0f, 0f, Mathf.Clamp01(Since / fadeSeconds)));

        GUI.matrix = prevMatrix;
        GUI.color = prevColor;
    }

    void HandleKeys()
    {
        var e = Event.current;
        if (e.type != EventType.KeyDown)
            return;
        bool enter = e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.character == '\n' || e.character == '\r';
        if (enter && phase == Phase.Writing)
        {
            Board();
            e.Use();
        }
        else if (e.keyCode == KeyCode.Escape && phase == Phase.Boarding)
        {
            phase = Phase.Writing; // changed your mind at the gate
            e.Use();
        }
    }

    void Title(float w)
    {
        // "Scenar" in paper, ".io" in the stamp red: the only two colours on the desk.
        var a = new GUIContent("Scenar");
        var b = new GUIContent(".io");
        float aw = wordmark.CalcSize(a).x, bw = wordmark.CalcSize(b).x;
        float x = (w - aw - bw) / 2f;
        var row = new Rect(x, 88f, aw, 100f);
        wordmark.normal.textColor = paper;
        GUI.Label(row, a, wordmark);
        row.x += aw;
        row.width = bw;
        wordmark.normal.textColor = accent;
        GUI.Label(row, b, wordmark);

        tagline.normal.textColor = new Color(paper.r, paper.g, paper.b, 0.6f);
        GUI.Label(new Rect(0f, 186f, w, 30f), "Say where you'd like to be. We build it; you talk your way through.", tagline);
    }

    void Pass(Rect pass)
    {
        // Paper with a shadow, header bar, and the stub torn off along a perforation.
        Fill(new Rect(pass.x + 6f, pass.y + 10f, pass.width, pass.height), new Color(0f, 0f, 0f, 0.45f));
        Fill(pass, paper);
        Fill(new Rect(pass.x, pass.y, pass.width, HeaderH), accent);

        var main = new Rect(pass.x, pass.y, pass.width - StubW, pass.height);
        var stub = new Rect(main.xMax, pass.y, StubW, pass.height);
        Perforation(main.xMax, pass.y, pass.height);

        Main(main);
        Stub(stub);

        if (Stage >= BoardingStage)
            Stamp("BUEN VIAJE", new Vector2(main.xMax - 170f, main.y + 150f), Mathf.Clamp01(StageSince * 4f));
    }

    void Main(Rect r)
    {
        float x = r.x + Pad, inner = r.width - Pad * 2f;

        Text(new Rect(x, r.y, inner, HeaderH), "BOARDING PASS", fine, paper, TextAnchor.MiddleLeft);
        Text(new Rect(x, r.y, inner, HeaderH), DateTime.Now.ToString("ddd dd MMM · HH:mm").ToUpperInvariant(), fine, new Color(paper.r, paper.g, paper.b, 0.75f), TextAnchor.MiddleRight);

        // 1. One line: where, and what for.
        float y = r.y + HeaderH + 30f;
        Text(new Rect(x, y, inner, 16f), "WHERE WOULD YOU LIKE TO BE, AND WHAT FOR?", label, faintInk);
        y += 22f;
        var lineRect = new Rect(x, y, inner, 34f);
        if (phase == Phase.Writing)
        {
            if (string.IsNullOrEmpty(line))
                GUI.Label(lineRect, example, placeholder);
            GUI.SetNextControlName("line");
            line = GUI.TextField(lineRect, line, 120, field);
            if (Event.current.type == EventType.Repaint && GUI.GetNameOfFocusedControl() != "line")
                GUI.FocusControl("line");
        }
        else
            Text(lineRect, Line, field, ink);
        y += 36f;
        DashedRule(x, y, inner, horizontal: true);

        // 2. One tap: how much of the language they have.
        y += 30f;
        Text(new Rect(x, y, inner, 16f), "AND HOW MUCH OF THE LANGUAGE DO YOU HAVE?", label, faintInk);
        y += 24f;
        float cx = x;
        for (int i = 0; i < levels.Length; i++)
        {
            var content = new GUIContent(levels[i].label);
            float cw = chip.CalcSize(content).x + 22f;
            var box = new Rect(cx, y, cw, 30f);
            bool on = i == level;
            if (on)
                Fill(box, ink);
            else
                Frame(box, faintInk, 1.5f);
            Text(box, content.text, chip, on ? paper : ink, TextAnchor.MiddleCenter);
            if (phase == Phase.Writing && GUI.Button(box, GUIContent.none, GUIStyle.none))
                level = i;
            cx += cw + 8f;
        }

        // Fine print: who does what on this trip.
        Text(new Rect(x, r.yMax - 34f, inner, 16f), "WORLDS · WORLD LABS MARBLE     VOICES · ELEVENLABS     LEVEL · " + Cefr, fine, faintInk);
    }

    void Stub(Rect r)
    {
        float x = r.x + Pad * 0.75f, inner = r.width - Pad * 1.5f;
        Text(new Rect(x, r.y, inner, HeaderH), "SCENAR.IO", fine, paper, TextAnchor.MiddleLeft);

        // Gate and seat fill in as the trip comes together.
        float y = r.y + HeaderH + 18f;
        Text(new Rect(x, y, inner / 2f, 16f), "GATE", label, faintInk);
        Text(new Rect(x + inner / 2f, y, inner / 2f, 16f), "SEAT", label, faintInk);
        y += 16f;
        string gateNow = Stage >= BoardingStage ? Flap(gate, StageSince) : "—";
        string seatNow = Stage >= CastingStage ? Flap(seat, Stage == CastingStage ? StageSince : 99f) : "—";
        Text(new Rect(x, y, inner / 2f, 30f), gateNow, value, gateNow == "—" ? faintInk : ink);
        Text(new Rect(x + inner / 2f, y, inner / 2f, 30f), seatNow, value, seatNow == "—" ? faintInk : ink);

        y += 46f;
        Text(new Rect(x, y, inner, 16f), "STATUS", label, faintInk);
        y += 16f;
        string status = phase == Phase.Writing ? "AT DESK"
                      : phase == Phase.Departing ? "DEPARTED"
                      : Flap(Stages[Stage].flap, StageSince);
        Text(new Rect(x, y, inner, 28f), status, flap, phase == Phase.Writing ? faintInk : accent);

        Barcode(new Rect(x, y + 44f, inner, 42f));

        // The button is the last thing on the stub; while boarding it becomes the progress bar.
        var box = new Rect(x, r.yMax - 60f, inner, 40f);
        if (phase == Phase.Writing)
        {
            Fill(box, accent);
            Text(box, "BOARD", button, paper, TextAnchor.MiddleCenter);
            if (GUI.Button(box, GUIContent.none, GUIStyle.none))
                Board();
            Text(new Rect(x, box.yMax + 2f, inner, 14f), "or press Enter", fine, faintInk, TextAnchor.MiddleCenter);
        }
        else
        {
            float progress = phase == Phase.Departing ? 1f : Mathf.Clamp01(Since / (Stages.Length * secondsPerStage));
            Frame(box, faintInk, 1.5f);
            Fill(new Rect(box.x + 4f, box.y + 4f, (box.width - 8f) * progress, box.height - 8f), accent);
        }
    }

    void Footnote(float w, float y)
    {
        string text = phase == Phase.Writing
            ? "One line is plenty. Pick a level, press Enter."
            : Stages[Stage].note;
        float alpha = phase == Phase.Writing ? 0.55f : 0.85f * Mathf.Clamp01(StageSince * 3f);
        note.normal.textColor = new Color(paper.r, paper.g, paper.b, alpha);
        GUI.Label(new Rect(0f, y, w, 30f), text, note);
    }

    // ---- bits of paper -------------------------------------------------------------

    /// <summary>Split-flap: letters spin and settle left to right over the first half second.</summary>
    string Flap(string target, float since)
    {
        var sb = new StringBuilder(target.Length);
        int tick = Mathf.FloorToInt(Time.time * 24f);
        for (int i = 0; i < target.Length; i++)
        {
            bool settled = since >= 0.2f + i * 0.08f || target[i] == ' ';
            sb.Append(settled ? target[i] : Alphabet[Mathf.Abs(tick * 7 + i * 13) % Alphabet.Length]);
        }
        return sb.ToString();
    }

    /// <summary>Bars from a hash of the line, so the pass changes as you type.</summary>
    void Barcode(Rect r)
    {
        int seed = 17;
        foreach (char c in Line)
            seed = unchecked(seed * 31 + c);
        var rng = new System.Random(seed);
        GUI.color = ink;
        for (float x = r.x; x < r.xMax - 3f;)
        {
            float bar = rng.Next(1, 4) * 1.1f;
            GUI.DrawTexture(new Rect(x, r.y, bar, r.height), Texture2D.whiteTexture);
            x += bar + rng.Next(1, 3) * 1.3f;
        }
        GUI.color = Color.white;
    }

    void Perforation(float x, float y, float height)
    {
        DashedRule(x, y, height, horizontal: false);
        // Notches where the stub tears off, punched through in the backdrop colour.
        GUI.color = backdrop;
        GUI.DrawTexture(new Rect(x - 11f, y - 11f, 22f, 22f), disc);
        GUI.DrawTexture(new Rect(x - 11f, y + height - 11f, 22f, 22f), disc);
        GUI.color = Color.white;
    }

    void DashedRule(float x, float y, float length, bool horizontal)
    {
        GUI.color = faintInk;
        for (float d = 0f; d < length; d += 9f)
            GUI.DrawTexture(horizontal ? new Rect(x + d, y, 5f, 1.5f) : new Rect(x, y + d, 1.5f, 5f), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    /// <summary>A rubber stamp across the pass, the same hand as the receipt's PAGADO.</summary>
    void Stamp(string text, Vector2 centre, float alpha)
    {
        var size = stamp.CalcSize(new GUIContent(text));
        var box = new Rect(centre.x - size.x / 2f - 14f, centre.y - size.y / 2f - 2f, size.x + 28f, size.y + 4f);
        var saved = GUI.matrix;
        GUI.matrix *= Matrix4x4.TRS(centre, Quaternion.Euler(0f, 0f, -8f), Vector3.one) * Matrix4x4.Translate(-centre);
        var red = new Color(accent.r, accent.g, accent.b, 0.85f * alpha);
        Frame(box, red, 3f);
        Text(box, text, stamp, red, TextAnchor.MiddleCenter);
        GUI.matrix = saved;
    }

    void Text(Rect r, string s, GUIStyle style, Color color, TextAnchor anchor = TextAnchor.MiddleLeft)
    {
        style.normal.textColor = color;
        style.alignment = anchor;
        GUI.Label(r, s, style);
    }

    static void Fill(Rect r, Color color)
    {
        GUI.color = color;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    static void Frame(Rect r, Color color, float b)
    {
        GUI.color = color;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, b), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.yMax - b, r.width, b), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.y, b, r.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.xMax - b, r.y, b, r.height), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    void EnsureStyles()
    {
        if (wordmark != null)
            return;
        sans = Font.CreateDynamicFontFromOSFont(
            new[] { "Helvetica Neue", "Helvetica", "Arial", "Liberation Sans", "DejaVu Sans" }, 16) ?? GUI.skin.font;
        mono = Font.CreateDynamicFontFromOSFont(
            new[] { "Courier New", "Liberation Mono", "DejaVu Sans Mono", "Menlo", "Consolas", "Nimbus Mono PS", "Courier" }, 16) ?? GUI.skin.font;

        GUIStyle Plain(Font f, int size, FontStyle weight = FontStyle.Normal) =>
            new(GUI.skin.label) { font = f, fontSize = size, fontStyle = weight, richText = false, wordWrap = false, padding = new RectOffset(0, 0, 0, 0) };

        wordmark = Plain(sans, 84, FontStyle.Bold);
        tagline = Plain(sans, 20);
        tagline.alignment = TextAnchor.MiddleCenter;
        label = Plain(sans, 11, FontStyle.Bold);
        fine = Plain(sans, 11, FontStyle.Bold);
        chip = Plain(mono, 14);
        value = Plain(mono, 24, FontStyle.Bold);
        flap = Plain(mono, 22, FontStyle.Bold);
        button = Plain(sans, 15, FontStyle.Bold);
        note = Plain(sans, 17);
        note.alignment = TextAnchor.MiddleCenter;
        stamp = Plain(mono, 30, FontStyle.Bold);

        // The line is typed straight onto the paper: no box, the ink for a caret.
        field = new GUIStyle(GUI.skin.textField) { font = mono, fontSize = 22, richText = false, padding = new RectOffset(0, 0, 0, 0), alignment = TextAnchor.MiddleLeft };
        foreach (var state in new[] { field.normal, field.hover, field.active, field.focused, field.onNormal, field.onHover, field.onActive, field.onFocused })
        {
            state.background = null;
            state.textColor = ink;
        }
        GUI.skin.settings.cursorColor = ink;
        GUI.skin.settings.cursorFlashSpeed = 1.2f;
        GUI.skin.settings.selectionColor = new Color(accent.r, accent.g, accent.b, 0.35f);
        placeholder = Plain(mono, 22);
        placeholder.normal.textColor = faintInk;

        // A disc for the tear-off notches, anti-aliased at the rim.
        const int n = 32;
        disc = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        for (int px = 0; px < n; px++)
            for (int py = 0; py < n; py++)
            {
                float d = Vector2.Distance(new Vector2(px + 0.5f, py + 0.5f), new Vector2(n / 2f, n / 2f));
                disc.SetPixel(px, py, new Color(1f, 1f, 1f, Mathf.Clamp01(n / 2f - d)));
            }
        disc.Apply();
    }
}
