using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The front of Scenar.io: one sentence with two blanks, and a globe.
///
///     Put me in ______________________.
///     I speak it [a little].
///
/// You finish the sentence; that is the whole configuration. Name a place and a pin
/// drops on the wireframe globe and it turns to face you. Enter: the globe rushes up
/// to the pin and dissolves, and in its place a room drafts itself in line by line,
/// the way a world comes out of World Labs Marble — floor, walls, window, counter,
/// table, two chairs — while a few words say what is being done (scouting, building,
/// casting the voices, writing the goals). Then the room is real: the destination
/// scene loads.
///
/// Every sentence lands in <see cref="destinationScene"/> for now; Café Nader is the
/// worked example of a generated trip. The sentence and level are kept in PlayerPrefs
/// (<see cref="LastLine"/>, <see cref="LastLevel"/>) for when generation is wired to
/// <c>POST /v1/scenarios</c>. Type is IMGUI; the globe and room are LineRenderers
/// built at start, so nothing here needs an asset.
/// </summary>
public class Wish : MonoBehaviour
{
    [Header("Destination")]
    [Tooltip("Scene every sentence lands in: the worked example of a generated world.")]
    public string destinationScene = "Assets/Scenes/CancunCafe.unity";

    [Header("The sentence")]
    [Tooltip("Shown faintly in the empty blank; used as the answer if the learner goes without typing.")]
    public string example = "a café in Cancún, ordering breakfast";
    public Level[] levels =
    {
        new() { word = "barely", cefr = "A1" },
        new() { word = "a little", cefr = "A2" },
        new() { word = "comfortably", cefr = "B1" },
        new() { word = "well", cefr = "B2" },
    };
    public int defaultLevel = 1;

    [Header("Look")]
    public Color backdrop = new(0.043f, 0.051f, 0.071f);
    public Color type = Color.white;
    [Tooltip("Blank underline, level word, pin.")]
    public Color accent = new(1f, 0.49f, 0.25f);
    [Tooltip("Globe and room wireframe.")]
    public Color wire = new(0.45f, 0.62f, 0.75f, 0.3f);
    public Vector3 globeCentre = new(2.0f, -0.35f, 0f);
    public float globeRadius = 1.7f;
    [Tooltip("Degrees per second the globe idles at before a place is named.")]
    public float idleSpin = 5f;

    [Header("Timing")]
    [Tooltip("Seconds from Enter to the scene load. The stages are spaced across it.")]
    public float tripSeconds = 8f;
    public float fadeSeconds = 0.8f;

    [Serializable]
    public class Level
    {
        public string word;
        [Tooltip("CEFR level sent with the scenario request.")]
        public string cefr;
    }

    /// <summary>What the learner last went with; empty until they have.</summary>
    public static string LastLine => PlayerPrefs.GetString(LinePref, "");
    public static string LastLevel => PlayerPrefs.GetString(LevelPref, "");

    const string LinePref = "wish.line";
    const string LevelPref = "wish.level";

    // Places the globe knows. Anything else gets a pin from a hash of the line; the
    // demo's café is the first one.
    static readonly (string key, string name, float lat, float lon)[] Places =
    {
        ("cancun", "Cancún, México", 21.16f, -86.85f),
        ("mexico city", "Ciudad de México", 19.43f, -99.13f), ("cdmx", "Ciudad de México", 19.43f, -99.13f),
        ("ciudad de mexico", "Ciudad de México", 19.43f, -99.13f),
        ("oaxaca", "Oaxaca, México", 17.07f, -96.72f), ("guadalajara", "Guadalajara, México", 20.67f, -103.35f),
        ("madrid", "Madrid, España", 40.42f, -3.70f), ("barcelona", "Barcelona, España", 41.39f, 2.17f),
        ("sevill", "Sevilla, España", 37.39f, -5.99f), ("buenos aires", "Buenos Aires, Argentina", -34.60f, -58.38f),
        ("bogota", "Bogotá, Colombia", 4.71f, -74.07f), ("lima", "Lima, Perú", -12.05f, -77.04f),
        ("santiago", "Santiago, Chile", -33.45f, -70.67f), ("havana", "La Habana, Cuba", 23.11f, -82.37f),
        ("la habana", "La Habana, Cuba", 23.11f, -82.37f), ("paris", "Paris, France", 48.86f, 2.35f),
        ("roma", "Roma, Italia", 41.90f, 12.50f), ("rome", "Roma, Italia", 41.90f, 12.50f),
        ("lisbo", "Lisboa, Portugal", 38.72f, -9.14f),
        ("sao paulo", "São Paulo, Brasil", -23.55f, -46.63f), ("tokyo", "Tokyo, Japan", 35.68f, 139.69f),
        ("berlin", "Berlin, Deutschland", 52.52f, 13.41f), ("seoul", "Seoul, Korea", 37.57f, 126.98f),
        ("london", "London, UK", 51.51f, -0.13f), ("new york", "New York, USA", 40.71f, -74.01f),
        ("montreal", "Montréal, Canada", 45.50f, -73.57f),
    };

    /// <summary>What is said while the room drafts in, as fractions of the trip.</summary>
    static readonly (float at, string title, string sub)[] Stages =
    {
        (0.00f, "Scouting {0}", "reading your line for where you are and who's there"),
        (0.19f, "Building the world", "World Labs Marble, from your line"),
        (0.41f, "Casting the locals", "ElevenLabs voices, and a reason to talk to you"),
        (0.61f, "Writing your three goals", "small enough to finish in one visit"),
        (0.79f, "Go.", "walk up to anyone and press E to talk"),
    };
    // Fractions of the trip: the globe rushes in and fades, the room drafts itself.
    const float ZoomFrom = 0.11f, ZoomTo = 0.30f, DraftFrom = 0.19f, DraftTo = 0.58f;

    enum Phase { Writing, Boarding, Departing }

    Phase phase = Phase.Writing;
    float phaseStart;
    string line = "";
    int level;
    bool loading;

    // The globe.
    Transform globe;
    readonly List<(LineRenderer line, float alpha)> globeLines = new();
    LineRenderer pin;
    float yaw, yawTarget;
    bool hasPin;
    string placeName = "";
    float pinLat, pinLon;

    // The room.
    Transform room;
    readonly List<(LineRenderer line, Vector3 a, Vector3 b)> roomLines = new();
    float roomSpin;

    Material wireMaterial;
    Camera cam;

    // Type, laid out on a 1280×720 canvas and scaled to the window.
    const float DesignW = 1280f, DesignH = 720f;
    Font sans;
    GUIStyle wordmark, tagline, sentence, blank, placeholder, hint, tiny, status, statusSub, pinLabel;

    string Line => string.IsNullOrWhiteSpace(line) ? example : line.Trim();
    string Cefr => levels.Length > 0 ? levels[Mathf.Clamp(level, 0, levels.Length - 1)].cefr : "";
    string LevelWord => levels.Length > 0 ? levels[Mathf.Clamp(level, 0, levels.Length - 1)].word : "";
    float Since => Time.time - phaseStart;
    /// <summary>Progress through the trip, 0–1; 0 while writing.</summary>
    float Trip => phase == Phase.Writing ? 0f : phase == Phase.Departing ? 1f : Mathf.Clamp01(Since / tripSeconds);

    void Start()
    {
        level = Mathf.Clamp(defaultLevel, 0, Mathf.Max(0, levels.Length - 1));
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        cam = Camera.main;

        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
        wireMaterial = new Material(shader);
        BuildGlobe();
        BuildRoom();
        SetAlpha(roomLines, 0f);
        yaw = yawTarget = 40f;
    }

    void OnDestroy()
    {
        if (wireMaterial != null)
            Destroy(wireMaterial);
    }

    // ---- the trip -------------------------------------------------------------------

    void Update()
    {
        if (phase == Phase.Boarding && Since >= tripSeconds)
        {
            phase = Phase.Departing;
            phaseStart = Time.time;
        }
        else if (phase == Phase.Departing && Since >= fadeSeconds)
            Load();

        float t = Trip;

        // Globe: idle spin until a place is named, then turn it to face you; on Enter,
        // rush up to the pin and fade out.
        if (phase == Phase.Writing)
            yawTarget = hasPin ? pinLon + 25f : yawTarget + idleSpin * Time.deltaTime; // pin a little left of centre, toward the sentence
        else
            yawTarget = pinLon;
        yaw = Mathf.LerpAngle(yaw, yawTarget, 1f - Mathf.Exp(-3f * Time.deltaTime));
        globe.rotation = Quaternion.Euler(-12f, 0f, 0f) * Quaternion.Euler(0f, yaw, 0f);

        float zoom = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ZoomFrom, ZoomTo, t));
        float scale = Mathf.Lerp(1f, 4f, zoom);
        globe.localScale = Vector3.one * scale;
        // Slide the globe so the pin ends up in front of the camera as it grows.
        var pinWorldDir = globe.rotation * PinLocal(pinLat, pinLon);
        var zoomed = new Vector3(0.6f, -0.1f, 0f) - pinWorldDir * globeRadius * scale;
        globe.position = Vector3.Lerp(globeCentre, zoomed, zoom);
        SetAlpha(globeLines, 1f - zoom);
        if (pin != null)
            Tint(pin, accent, 1f - zoom);

        // Room: drafts itself in line by line while the stages read out.
        float draft = Mathf.InverseLerp(DraftFrom, DraftTo, t);
        roomSpin += (phase == Phase.Writing ? 0f : 9f) * Time.deltaTime;
        room.rotation = Quaternion.Euler(-24f, 0f, 0f) * Quaternion.Euler(0f, roomSpin, 0f);
        Draft(draft);
        SetAlpha(roomLines, Mathf.Clamp01(draft * 4f));
    }

    void Go()
    {
        if (phase != Phase.Writing)
            return;
        if (!hasPin)
            Pin(Line.GetHashCode(), "the place"); // no city we know: still somewhere
        phase = Phase.Boarding;
        phaseStart = Time.time;
        GUI.FocusControl(null);
        PlayerPrefs.SetString(LinePref, Line);
        PlayerPrefs.SetString(LevelPref, Cefr);
        PlayerPrefs.Save();
        Debug.Log($"Wish: «{Line}», speaks it {LevelWord} ({Cefr})");
    }

    void ChangeMind()
    {
        phase = Phase.Writing;
        roomSpin = 0f;
        Place(Line); // put the pin back where the text says, or nowhere
    }

    void Load()
    {
        if (loading)
            return;
        loading = true;
        Debug.Log($"Wish: landing in {destinationScene}");
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

    // ---- the globe ------------------------------------------------------------------

    void BuildGlobe()
    {
        globe = new GameObject("Globe").transform;
        globe.SetParent(transform, false);
        globe.position = globeCentre;

        const int n = 96;
        var ring = new Vector3[n];
        // Meridians: six great circles through the poles.
        for (int m = 0; m < 6; m++)
        {
            var turn = Quaternion.AngleAxis(-m * 30f, Vector3.up); // same sense as PinLocal's longitude
            for (int i = 0; i < n; i++)
            {
                float a = i * 2f * Mathf.PI / n;
                ring[i] = turn * new Vector3(0f, Mathf.Sin(a), -Mathf.Cos(a)) * globeRadius;
            }
            globeLines.Add((Wire(globe, wire, 0.008f, true, ring), wire.a));
        }
        // Parallels: equator (a touch brighter) and ±30°, ±60°.
        foreach (float lat in new[] { -60f, -30f, 0f, 30f, 60f })
        {
            for (int i = 0; i < n; i++)
                ring[i] = PinLocal(lat, i * 360f / n) * globeRadius;
            float a = lat == 0f ? wire.a * 1.6f : wire.a;
            globeLines.Add((Wire(globe, new Color(wire.r, wire.g, wire.b, a), lat == 0f ? 0.011f : 0.008f, true, ring), a));
        }
    }

    /// <summary>Unit vector for a latitude/longitude; longitude 0 faces the camera.</summary>
    static Vector3 PinLocal(float lat, float lon)
    {
        float la = lat * Mathf.Deg2Rad, lo = lon * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(la) * Mathf.Sin(lo), Mathf.Sin(la), -Mathf.Cos(la) * Mathf.Cos(lo));
    }

    /// <summary>Read the sentence for a place the globe knows and pin it.</summary>
    void Place(string text)
    {
        string plain = Plain(text);
        foreach (var p in Places)
            if (Regex.IsMatch(plain, @"\b" + Regex.Escape(p.key)))
            {
                if (!hasPin || placeName != p.name)
                    Pin(p.lat, p.lon, p.name);
                return;
            }
        if (hasPin)
        {
            Destroy(pin.gameObject);
            pin = null;
            hasPin = false;
            placeName = "";
        }
    }

    void Pin(int seed, string name)
    {
        var rng = new System.Random(seed);
        Pin((float)rng.NextDouble() * 110f - 55f, (float)rng.NextDouble() * 360f - 180f, name);
    }

    void Pin(float lat, float lon, string name)
    {
        if (pin != null)
            Destroy(pin.gameObject);
        pinLat = lat;
        pinLon = lon;
        placeName = name;
        hasPin = true;
        var dir = PinLocal(lat, lon);
        pin = Wire(globe, accent, 0.03f, false, dir * globeRadius, dir * (globeRadius + 0.22f));
    }

    static string Plain(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    // ---- the room -------------------------------------------------------------------

    void BuildRoom()
    {
        room = new GameObject("Room").transform;
        room.SetParent(transform, false);
        room.position = new Vector3(0.2f, -0.85f, 0f);
        room.localScale = Vector3.one * 0.36f;

        // Drafting order: floor, walls, ceiling, window, counter, table, chairs.
        const float w = 3f, d = 2.5f, h = 2.8f;
        Seg(new(-w, 0, -d), new(w, 0, -d)); Seg(new(w, 0, -d), new(w, 0, d));
        Seg(new(w, 0, d), new(-w, 0, d)); Seg(new(-w, 0, d), new(-w, 0, -d));
        foreach (var (x, z) in new[] { (-w, -d), (w, -d), (w, d), (-w, d) })
            Seg(new(x, 0, z), new(x, h, z));
        Seg(new(-w, h, -d), new(w, h, -d)); Seg(new(w, h, -d), new(w, h, d));
        Seg(new(w, h, d), new(-w, h, d)); Seg(new(-w, h, d), new(-w, h, -d));
        Quad(new(-2.3f, 1.0f, d), new(-0.5f, 1.0f, d), new(-0.5f, 2.2f, d), new(-2.3f, 2.2f, d));   // window
        Box(0.4f, 0f, 1.7f, 2.6f, 1.0f, 2.35f);                                                      // counter
        Box(-1.7f, 0.72f, -0.8f, -0.7f, 0.78f, 0.2f);                                                 // table top
        foreach (var (x, z) in new[] { (-1.62f, -0.72f), (-0.78f, -0.72f), (-0.78f, 0.12f), (-1.62f, 0.12f) })
            Seg(new(x, 0, z), new(x, 0.72f, z));                                                      // legs
        Chair(-2.4f, -0.3f, +1); Chair(0.0f, -0.3f, -1);
    }

    void Seg(Vector3 a, Vector3 b) => roomLines.Add((Wire(room, wire, 0.02f, false, a, a), a, b));

    void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        Seg(a, b); Seg(b, c); Seg(c, d); Seg(d, a);
    }

    void Box(float x0, float y0, float z0, float x1, float y1, float z1)
    {
        Quad(new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1));
        Quad(new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1));
        Seg(new(x0, y0, z0), new(x0, y1, z0)); Seg(new(x1, y0, z0), new(x1, y1, z0));
        Seg(new(x1, y0, z1), new(x1, y1, z1)); Seg(new(x0, y0, z1), new(x0, y1, z1));
    }

    /// <summary>A seat with a back on the side away from the table (<paramref name="facing"/> = ±1 along x).</summary>
    void Chair(float x, float z, int facing)
    {
        const float s = 0.22f;
        Box(x - s, 0.42f, z - s, x + s, 0.47f, z + s);
        foreach (var (dx, dz) in new[] { (-s, -s), (s, -s), (s, s), (-s, s) })
            Seg(new(x + dx, 0, z + dz), new(x + dx, 0.42f, z + dz));
        float bx = x - facing * s;
        Quad(new(bx, 0.47f, z - s), new(bx, 0.47f, z + s), new(bx, 0.95f, z + s), new(bx, 0.95f, z - s));
    }

    /// <summary>Draw the room up to <paramref name="p"/> of the way, the current line part-way.</summary>
    void Draft(float p)
    {
        int n = roomLines.Count;
        for (int i = 0; i < n; i++)
        {
            float f = Mathf.Clamp01(p * n - i);
            var (lr, a, b) = roomLines[i];
            lr.SetPosition(1, Vector3.Lerp(a, b, f));
            lr.enabled = f > 0f;
        }
    }

    // ---- lines ----------------------------------------------------------------------

    LineRenderer Wire(Transform parent, Color color, float width, bool loop, params Vector3[] points)
    {
        var go = new GameObject("line");
        go.transform.SetParent(parent, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.loop = loop;
        lr.material = wireMaterial;
        lr.widthMultiplier = width;
        lr.numCapVertices = 2;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.positionCount = points.Length;
        lr.SetPositions(points);
        Tint(lr, color, 1f);
        return lr;
    }

    static void Tint(LineRenderer lr, Color color, float alpha)
    {
        var c = new Color(color.r, color.g, color.b, color.a * alpha);
        lr.startColor = lr.endColor = c;
    }

    void SetAlpha(List<(LineRenderer line, float alpha)> lines, float alpha)
    {
        foreach (var (lr, baseAlpha) in lines)
            lr.startColor = lr.endColor = new Color(wire.r, wire.g, wire.b, baseAlpha * alpha);
    }

    void SetAlpha(List<(LineRenderer line, Vector3 a, Vector3 b)> lines, float alpha)
    {
        foreach (var (lr, _, _) in lines)
            lr.startColor = lr.endColor = new Color(wire.r, wire.g, wire.b, wire.a * 2.2f * alpha);
    }

    // ---- type -----------------------------------------------------------------------

    void OnGUI()
    {
        EnsureStyles();
        HandleKeys();

        var prevMatrix = GUI.matrix;
        var prevColor = GUI.color;
        float scale = Mathf.Min(Screen.width / DesignW, Screen.height / DesignH);
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        float w = Screen.width / scale, h = Screen.height / scale;

        Wordmark(w);
        Sentence(1f - Mathf.Clamp01(Trip * tripSeconds / 0.6f));
        PinLabel(scale);
        if (phase != Phase.Writing)
            Status(w);
        if (phase == Phase.Departing)
        {
            GUI.color = new Color(0f, 0f, 0f, Mathf.Clamp01(Since / fadeSeconds));
            GUI.DrawTexture(new Rect(0f, 0f, w, h), Texture2D.whiteTexture);
        }

        GUI.matrix = prevMatrix;
        GUI.color = prevColor;
    }

    void HandleKeys()
    {
        var e = Event.current;
        if (e.type != EventType.KeyDown)
            return;
        bool enter = e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.character == '\n' || e.character == '\r';
        if (phase == Phase.Writing)
        {
            if (enter) { Go(); e.Use(); }
            else if (e.keyCode == KeyCode.UpArrow) { level = (level + levels.Length - 1) % levels.Length; e.Use(); }
            else if (e.keyCode == KeyCode.DownArrow) { level = (level + 1) % levels.Length; e.Use(); }
        }
        else if (phase == Phase.Boarding && e.keyCode == KeyCode.Escape)
        {
            ChangeMind();
            e.Use();
        }
    }

    void Wordmark(float w)
    {
        var a = new GUIContent("Scenar");
        var b = new GUIContent(".io");
        float aw = wordmark.CalcSize(a).x, bw = wordmark.CalcSize(b).x;
        var row = new Rect((w - aw - bw) / 2f, 54f, aw, 84f);
        wordmark.normal.textColor = type;
        GUI.Label(row, a, wordmark);
        row.x += aw;
        row.width = bw;
        wordmark.normal.textColor = accent;
        GUI.Label(row, b, wordmark);
        tagline.normal.textColor = new Color(type.r, type.g, type.b, 0.5f);
        GUI.Label(new Rect(0f, 138f, w, 24f), "A world for whatever you need to say.", tagline);
    }

    void Sentence(float alpha)
    {
        if (alpha <= 0f)
            return;
        const float x = 96f, width = 600f;
        float y = 262f;
        var ink = new Color(type.r, type.g, type.b, alpha);
        var faint = new Color(type.r, type.g, type.b, 0.38f * alpha);
        var orange = new Color(accent.r, accent.g, accent.b, alpha);

        Text(new Rect(x, y, width, 46f), "Put me in", sentence, ink);
        y += 60f;

        // The blank: typed straight onto the dark, underlined in the accent.
        var blankRect = new Rect(x, y, width, 46f);
        if (phase == Phase.Writing)
        {
            if (string.IsNullOrEmpty(line))
                Text(blankRect, example, placeholder, faint);
            GUI.SetNextControlName("blank");
            string was = line;
            line = GUI.TextField(blankRect, line, 120, blank);
            if (line != was)
                Place(line);
            if (Event.current.type == EventType.Repaint && GUI.GetNameOfFocusedControl() != "blank")
                GUI.FocusControl("blank");
        }
        else
            Text(blankRect, Line, blank, ink);
        GUI.color = orange;
        GUI.DrawTexture(new Rect(x, y + 50f, width, 2f), Texture2D.whiteTexture);
        GUI.color = Color.white;
        y += 82f;

        // The level: one word in the accent; click or ↑/↓ to change it.
        string lead = "I speak it ";
        float leadW = sentence.CalcSize(new GUIContent(lead)).x;
        float wordW = sentence.CalcSize(new GUIContent(LevelWord)).x;
        Text(new Rect(x, y, leadW + 4f, 46f), lead, sentence, ink);
        var wordRect = new Rect(x + leadW, y, wordW, 46f);
        Text(wordRect, LevelWord, sentence, orange);
        Text(new Rect(wordRect.xMax, y, 40f, 46f), ".", sentence, ink);
        GUI.color = orange;
        for (float d = 0f; d < wordW; d += 8f)
            GUI.DrawTexture(new Rect(wordRect.x + d, y + 50f, 4f, 2f), Texture2D.whiteTexture);
        GUI.color = Color.white;
        if (phase == Phase.Writing && GUI.Button(wordRect, GUIContent.none, GUIStyle.none))
            level = (level + 1) % levels.Length;
        Text(new Rect(wordRect.x, y + 56f, 200f, 16f), "click, or ↑ ↓", tiny, faint);

        y += 110f;
        Text(new Rect(x, y, width, 24f), "Enter to go.", hint, faint);
    }

    /// <summary>Name the pinned place beside its pin, wherever the globe has turned it.</summary>
    void PinLabel(float scale)
    {
        if (!hasPin || cam == null || Trip > ZoomFrom)
            return;
        var tip = globe.TransformPoint(PinLocal(pinLat, pinLon) * (globeRadius + 0.22f));
        // Only while the pin is on the near side; behind the globe it should not float.
        if (Vector3.Dot(tip - globe.position, cam.transform.forward) > 0f)
            return;
        var s = cam.WorldToScreenPoint(tip);
        var p = new Vector2(s.x / scale, (Screen.height - s.y) / scale);
        Text(new Rect(p.x + 10f, p.y - 22f, 320f, 20f), placeName, pinLabel, new Color(type.r, type.g, type.b, 0.8f));
    }

    void Status(float w)
    {
        float t = Trip;
        int i = 0;
        for (int k = 0; k < Stages.Length; k++)
            if (t >= Stages[k].at)
                i = k;
        float a = Mathf.Clamp01((t - Stages[i].at) * tripSeconds / 0.5f);
        string title = string.Format(Stages[i].title, placeName == "the place" ? "the place" : placeName.Split(',')[0]);
        Text(new Rect(0f, 200f, w, 56f), title, status, new Color(type.r, type.g, type.b, a), TextAnchor.MiddleCenter);
        Text(new Rect(0f, 256f, w, 26f), Stages[i].sub, statusSub, new Color(type.r, type.g, type.b, 0.5f * a), TextAnchor.MiddleCenter);
    }

    static void Text(Rect r, string s, GUIStyle style, Color color, TextAnchor anchor = TextAnchor.MiddleLeft)
    {
        style.normal.textColor = color;
        style.alignment = anchor;
        GUI.Label(r, s, style);
    }

    void EnsureStyles()
    {
        if (wordmark != null)
            return;
        sans = Font.CreateDynamicFontFromOSFont(
            new[] { "Helvetica Neue", "Helvetica", "Arial", "Liberation Sans", "DejaVu Sans" }, 16) ?? GUI.skin.font;

        GUIStyle Plain(int size, FontStyle weight = FontStyle.Normal) =>
            new(GUI.skin.label) { font = sans, fontSize = size, fontStyle = weight, richText = false, wordWrap = false, padding = new RectOffset(0, 0, 0, 0) };

        wordmark = Plain(76, FontStyle.Bold);
        tagline = Plain(17);
        tagline.alignment = TextAnchor.MiddleCenter;
        sentence = Plain(36);
        placeholder = Plain(36);
        hint = Plain(16);
        tiny = Plain(12);
        status = Plain(44);
        statusSub = Plain(18);
        pinLabel = Plain(14, FontStyle.Bold);

        blank = new GUIStyle(GUI.skin.textField) { font = sans, fontSize = 36, richText = false, padding = new RectOffset(0, 0, 0, 0), alignment = TextAnchor.MiddleLeft };
        foreach (var state in new[] { blank.normal, blank.hover, blank.active, blank.focused, blank.onNormal, blank.onHover, blank.onActive, blank.onFocused })
        {
            state.background = null;
            state.textColor = type;
        }
        GUI.skin.settings.cursorColor = accent;
        GUI.skin.settings.cursorFlashSpeed = 1.1f;
        GUI.skin.settings.selectionColor = new Color(accent.r, accent.g, accent.b, 0.35f);
    }
}
