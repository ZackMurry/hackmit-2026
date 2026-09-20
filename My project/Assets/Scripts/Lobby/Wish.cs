using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// The front of Scenar.io: one sentence with two blanks, and the Earth in the clouds.
///
///     Put me in ______________________.
///     I speak it [a little].
///
/// You finish the sentence; that is the whole configuration. Name a place and a pin
/// lands on the globe and it turns to face you. Enter: the globe swings the pin round
/// and rushes up to it while the clouds close in, until the screen is nearly white and
/// a few words say what is being done (scouting, building, casting the voices, writing
/// the goals). The destination loads behind the white, and the clouds part onto the
/// spawn (<see cref="CloudCurtain"/>).
///
/// Every sentence lands in <see cref="destinationScene"/> for now; Café Nader is the
/// worked example of a generated trip. The sentence and level are kept in PlayerPrefs
/// (<see cref="LastLine"/>, <see cref="LastLevel"/>) for when generation is wired to
/// <c>POST /v1/scenarios</c>. The sky is a Poly Haven HDRI and the Earth a NASA Blue
/// Marble, both under <c>Resources/Wish</c>; the type is IMGUI.
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
    [Tooltip("Type colour; dark, since the sky is bright.")]
    public Color ink = new(0.09f, 0.10f, 0.13f);
    [Tooltip("Blank underline, level word, pin.")]
    public Color accent = new(1f, 0.49f, 0.25f);
    [Tooltip("Heading of the sky panorama, degrees.")]
    public float skyRotation = 0f;
    public float skyExposure = 1.15f;
    public Vector3 globeCentre = new(2.0f, -0.15f, 0f);
    public float globeRadius = 1.7f;
    [Tooltip("Degrees per second the globe idles at before a place is named.")]
    public float idleSpin = 5f;

    [Header("Timing")]
    [Tooltip("Seconds from Enter to the scene load. The stages are spaced across it.")]
    public float tripSeconds = 8f;
    [Tooltip("Seconds the clouds take to open onto this scene when it starts.")]
    public float arriveSeconds = 2.4f;
    [Tooltip("Seconds the clouds take to part onto the destination's spawn.")]
    public float revealSeconds = 3.2f;

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

    /// <summary>What is said while the clouds close in, as fractions of the trip.</summary>
    static readonly (float at, string title, string sub)[] Stages =
    {
        (0.00f, "Scouting {0}", "reading your line for where you are and who's there"),
        (0.19f, "Building the world", "World Labs Marble, from your line"),
        (0.41f, "Casting the locals", "ElevenLabs voices, and a reason to talk to you"),
        (0.61f, "Writing your three goals", "small enough to finish in one visit"),
        (0.79f, "Go.", "walk up to anyone and press E to talk"),
    };
    // Fractions of the trip: the globe rushes up to the pin; the clouds close over it.
    const float ZoomFrom = 0.11f, ZoomTo = 0.34f, CoverFrom = 0.11f, CoverTo = 0.64f;

    enum Phase { Writing, Boarding, Departing }

    Phase phase = Phase.Writing;
    float phaseStart;
    string line = "";
    int level;
    bool loading;

    // The globe.
    Transform globe;
    Transform pin;
    float yaw, yawTarget;
    bool hasPin;
    string placeName = "";
    float pinLat, pinLon;

    Material skyMaterial, earthMaterial, pinMaterial;
    Light sun;
    Camera cam;
    CloudCurtain curtain;

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

        Sky();
        BuildGlobe();
        yaw = yawTarget = 40f;

        curtain = CloudCurtain.Get();
        curtain.Arrive(arriveSeconds);
    }

    void OnDestroy()
    {
        foreach (var m in new[] { skyMaterial, earthMaterial, pinMaterial })
            if (m != null)
                Destroy(m);
    }

    // ---- the trip -------------------------------------------------------------------

    void Update()
    {
        if (phase == Phase.Boarding && Since >= tripSeconds)
            Depart();

        float t = Trip;

        // Globe: idle spin until a place is named, then turn it to face you; on Enter,
        // bring the pin round and rush up to it.
        if (phase == Phase.Writing)
            yawTarget = hasPin ? pinLon + 25f : yawTarget + idleSpin * Time.deltaTime; // pin a little left of centre, toward the sentence
        else
            yawTarget = pinLon;
        yaw = Mathf.LerpAngle(yaw, yawTarget, 1f - Mathf.Exp(-3f * Time.deltaTime));
        globe.rotation = Quaternion.Euler(-12f, 0f, 0f) * Quaternion.Euler(0f, yaw, 0f);

        float zoom = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ZoomFrom, ZoomTo, t));
        float scale = Mathf.Lerp(1f, 3.5f, zoom);
        globe.localScale = Vector3.one * scale;
        // Slide the globe so the pin ends up in front of the camera as it grows.
        var pinWorldDir = globe.rotation * PinLocal(pinLat, pinLon);
        var zoomed = new Vector3(0.4f, -0.2f, 0f) - pinWorldDir * globeRadius * scale;
        globe.position = Vector3.Lerp(globeCentre, zoomed, zoom);

        if (pin != null)
            pin.localScale = Vector3.one * (0.075f + 0.02f * Mathf.Sin(Time.time * 4f));

        // The clouds close in as the trip goes on (and open again if you change your mind).
        if (curtain != null && phase != Phase.Departing)
            curtain.Cover = phase == Phase.Writing ? 0f : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(CoverFrom, CoverTo, t));
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
        Place(Line); // put the pin back where the text says, or nowhere
    }

    /// <summary>White out, load the destination behind it, and let the clouds part onto it.</summary>
    void Depart()
    {
        if (loading)
            return;
        loading = true;
        phase = Phase.Departing;
        phaseStart = Time.time;
        if (curtain != null)
            curtain.Reveal(revealSeconds);
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

    // ---- the sky and the globe ------------------------------------------------------

    void Sky()
    {
        var panorama = Resources.Load<Texture2D>("Wish/sky");
        var shader = Shader.Find("Skybox/Panoramic");
        if (panorama != null && shader != null)
        {
            skyMaterial = new Material(shader);
            skyMaterial.SetTexture("_MainTex", panorama);
            skyMaterial.SetFloat("_Mapping", 1f);       // latitude/longitude layout
            skyMaterial.EnableKeyword("_MAPPING_LATITUDE_LONGITUDE_LAYOUT");
            skyMaterial.DisableKeyword("_MAPPING_6_FRAMES_LAYOUT");
            skyMaterial.SetFloat("_ImageType", 0f);     // 360°
            skyMaterial.SetFloat("_Rotation", skyRotation);
            skyMaterial.SetFloat("_Exposure", skyExposure);
            RenderSettings.skybox = skyMaterial;
            if (cam != null)
                cam.clearFlags = CameraClearFlags.Skybox;
        }
        else
            Debug.LogWarning("Wish: no sky (Resources/Wish/sky or the Skybox/Panoramic shader is missing)");

        // Daylight from up and to the right, and a soft blue fill so the night side isn't black.
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.60f, 0.66f, 0.78f);
        sun = new GameObject("Sun").AddComponent<Light>();
        sun.transform.SetParent(transform, false);
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.97f, 0.92f);
        sun.intensity = 1.5f;
        sun.shadows = LightShadows.None;
        sun.transform.rotation = Quaternion.Euler(28f, -38f, 0f);
    }

    void BuildGlobe()
    {
        globe = new GameObject("Globe").transform;
        globe.SetParent(transform, false);
        globe.position = globeCentre;

        var earth = new GameObject("Earth");
        earth.transform.SetParent(globe, false);
        earth.transform.localScale = Vector3.one * globeRadius;
        earth.AddComponent<MeshFilter>().sharedMesh = SphereMesh(96, 48);
        var renderer = earth.AddComponent<MeshRenderer>();
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        earthMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
        var map = Resources.Load<Texture2D>("Wish/earth");
        if (map != null)
        {
            earthMaterial.SetTexture("_BaseMap", map);
            earthMaterial.SetTexture("_MainTex", map);   // in case the fallback shader is what we got
        }
        else
            Debug.LogWarning("Wish: no Earth texture at Resources/Wish/earth");
        earthMaterial.SetFloat("_Smoothness", 0.3f);
        earthMaterial.SetFloat("_Metallic", 0f);
        renderer.sharedMaterial = earthMaterial;

        pinMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color"));
        pinMaterial.SetColor("_BaseColor", accent);
        pinMaterial.SetColor("_Color", accent);
    }

    /// <summary>
    /// A UV sphere whose longitude 0 faces the camera and whose texture reads the right
    /// way round from outside: u runs with longitude (−180…180 → 0…1), v with latitude.
    /// </summary>
    static Mesh SphereMesh(int segments, int rings)
    {
        var verts = new Vector3[(rings + 1) * (segments + 1)];
        var uv = new Vector2[verts.Length];
        for (int i = 0; i <= rings; i++)
        {
            float lat = -90f + 180f * i / rings;
            for (int j = 0; j <= segments; j++)
            {
                float lon = -180f + 360f * j / segments;
                int k = i * (segments + 1) + j;
                verts[k] = PinLocal(lat, lon);
                uv[k] = new Vector2(j / (float)segments, i / (float)rings);
            }
        }
        var tris = new int[rings * segments * 6];
        int n = 0;
        for (int i = 0; i < rings; i++)
            for (int j = 0; j < segments; j++)
            {
                int a = i * (segments + 1) + j, b = a + 1, c = a + segments + 1, d = c + 1;
                // Clockwise seen from outside: Unity's front face.
                tris[n++] = a; tris[n++] = c; tris[n++] = d;
                tris[n++] = a; tris[n++] = d; tris[n++] = b;
            }
        var mesh = new Mesh { name = "Earth" };
        mesh.SetVertices(verts);
        mesh.SetNormals(verts);   // unit sphere: the position is the normal
        mesh.SetUVs(0, uv);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        return mesh;
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
        var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = "Pin";
        Destroy(marker.GetComponent<Collider>());
        marker.GetComponent<MeshRenderer>().sharedMaterial = pinMaterial;
        pin = marker.transform;
        pin.SetParent(globe, false);
        pin.localPosition = PinLocal(lat, lon) * (globeRadius * 1.005f);
    }

    static string Plain(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    // ---- type -----------------------------------------------------------------------

    void OnGUI()
    {
        EnsureStyles();
        HandleKeys();
        GUI.depth = -200;   // over the clouds

        var prevMatrix = GUI.matrix;
        var prevColor = GUI.color;
        float scale = Mathf.Min(Screen.width / DesignW, Screen.height / DesignH);
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        float w = Screen.width / scale;

        Wordmark(w);
        Sentence(1f - Mathf.Clamp01(Trip * tripSeconds / 0.6f));
        PinLabel(scale);
        if (phase != Phase.Writing)
            Status();

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
        wordmark.normal.textColor = ink;
        GUI.Label(row, a, wordmark);
        row.x += aw;
        row.width = bw;
        wordmark.normal.textColor = accent;
        GUI.Label(row, b, wordmark);
        tagline.normal.textColor = new Color(ink.r, ink.g, ink.b, 0.6f);
        GUI.Label(new Rect(0f, 138f, w, 24f), "A world for whatever you need to say.", tagline);
    }

    void Sentence(float alpha)
    {
        if (alpha <= 0f)
            return;
        const float x = 96f, width = 600f;
        float y = 262f;
        var dark = new Color(ink.r, ink.g, ink.b, alpha);
        var faint = new Color(ink.r, ink.g, ink.b, 0.42f * alpha);
        var orange = new Color(accent.r, accent.g, accent.b, alpha);

        Text(new Rect(x, y, width, 46f), "Put me in", sentence, dark);
        y += 60f;

        // The blank: typed straight onto the sky, underlined in the accent.
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
            Text(blankRect, Line, blank, dark);
        GUI.color = orange;
        GUI.DrawTexture(new Rect(x, y + 50f, width, 2f), Texture2D.whiteTexture);
        GUI.color = Color.white;
        y += 82f;

        // The level: one word in the accent; click or ↑/↓ to change it.
        string lead = "I speak it ";
        float leadW = sentence.CalcSize(new GUIContent(lead)).x;
        float wordW = sentence.CalcSize(new GUIContent(LevelWord)).x;
        Text(new Rect(x, y, leadW + 4f, 46f), lead, sentence, dark);
        var wordRect = new Rect(x + leadW, y, wordW, 46f);
        Text(wordRect, LevelWord, sentence, orange);
        Text(new Rect(wordRect.xMax, y, 40f, 46f), ".", sentence, dark);
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
        if (!hasPin || cam == null || pin == null || Trip > ZoomFrom)
            return;
        var tip = pin.position;
        // Only while the pin is on the near side; behind the globe it should not float.
        if (Vector3.Dot(tip - globe.position, cam.transform.forward) > 0f)
            return;
        var s = cam.WorldToScreenPoint(tip);
        var p = new Vector2(s.x / scale, (Screen.height - s.y) / scale);
        float tw = pinLabel.CalcSize(new GUIContent(placeName)).x;
        var r = new Rect(p.x + 12f, p.y - 24f, tw + 16f, 22f);
        GUI.color = new Color(1f, 1f, 1f, 0.85f);             // a small white tag, readable over the oceans
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = Color.white;
        Text(new Rect(r.x + 8f, r.y, tw, r.height), placeName, pinLabel, ink);
    }

    /// <summary>The stages read out where the sentence was, as the clouds close.</summary>
    void Status()
    {
        float t = Trip;
        int i = 0;
        for (int k = 0; k < Stages.Length; k++)
            if (t >= Stages[k].at)
                i = k;
        float a = Mathf.Clamp01((t - Stages[i].at) * tripSeconds / 0.5f);
        string title = string.Format(Stages[i].title, placeName == "the place" ? "the place" : placeName.Split(',')[0]);
        Text(new Rect(96f, 262f, 720f, 56f), title, status, new Color(ink.r, ink.g, ink.b, a));
        Text(new Rect(96f, 322f, 720f, 26f), Stages[i].sub, statusSub, new Color(ink.r, ink.g, ink.b, 0.6f * a));
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
        sans = PickFont(16);

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
            state.textColor = ink;
        }
        GUI.skin.settings.cursorColor = accent;
        GUI.skin.settings.cursorFlashSpeed = 1.1f;
        GUI.skin.settings.selectionColor = new Color(accent.r, accent.g, accent.b, 0.35f);
    }

    /// <summary>
    /// A sans the OS actually has, or Unity's built-in one. Asking for a face that isn't
    /// installed gives a font with no glyphs ("Can't generate mesh, no font asset").
    /// </summary>
    static Font PickFont(int size)
    {
        var installed = new HashSet<string>(Font.GetOSInstalledFontNames() ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "Helvetica Neue", "Arial", "Liberation Sans", "DejaVu Sans", "Noto Sans", "Segoe UI", "Roboto" })
            if (installed.Contains(name))
            {
                var f = Font.CreateDynamicFontFromOSFont(name, size);
                if (f != null && f.dynamic)
                    return f;
            }
        Font builtin = null;
        try { builtin = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (Exception) { }
        return builtin != null ? builtin : GUI.skin.font;
    }
}
