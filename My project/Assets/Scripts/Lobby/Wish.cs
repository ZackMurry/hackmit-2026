using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// The front of Scenar.io: one sentence with two blanks, and a low-poly Earth in the clouds.
///
///     Put me in ______________________.
///
/// You finish the sentence; that is the whole configuration. Name a place and a pin
/// lands on the globe and it turns to face you. Enter: the globe swings the pin round
/// and rushes up to it while you fly forward into the clouds — they stream past, close
/// in, and fill the screen white — and a few words, in the traveller's terms rather than
/// ours, say what is happening (heading there, setting the scene, meeting the locals, a
/// reason to be there, go). The destination loads behind the
/// white, and the clouds part onto the spawn (<see cref="CloudCurtain"/>).
///
/// Every sentence lands in <see cref="destinationScene"/> for now; Café Nader is the
/// worked example of a generated trip. The sentence is kept in PlayerPrefs
/// (<see cref="LastLine"/>) for when generation is wired to
/// <c>POST /v1/scenarios</c>. Everything in the scene is flat-shaded geometry built at
/// start (<see cref="LowPoly"/>): a gradient dome, a faceted Earth coloured from NASA's
/// land/sea map under <c>Resources/Wish</c>, and clouds of squashed icospheres. The type
/// is IMGUI.
/// </summary>
public class Wish : MonoBehaviour
{
    [Header("Destination")]
    [Tooltip("Scene every sentence lands in: the worked example of a generated world.")]
    public string destinationScene = "Assets/Scenes/CancunCafe.unity";

    [Header("The sentence")]
    [Tooltip("Shown faintly in the empty blank; used as the answer if the learner goes without typing.")]
    public string example = "a café in Cancún, ordering breakfast";

    [Header("Look")]
    [Tooltip("Type colour; dark, since the sky is bright.")]
    public Color ink = new(0.09f, 0.10f, 0.13f);
    public Color skyZenith = new(0.36f, 0.58f, 0.86f);
    public Color skyHorizon = new(0.84f, 0.90f, 0.97f);
    public Color skyBelow = new(0.70f, 0.80f, 0.92f);
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

    /// <summary>What the learner last went with; empty until they have.</summary>
    public static string LastLine => PlayerPrefs.GetString(LinePref, "");

    const string LinePref = "wish.line";

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
        (0.00f, "Heading for {0}", "somewhere real, with real people in it"),
        (0.19f, "Setting the scene", "the streets, the counter, the menu on the wall"),
        (0.41f, "Meeting the locals", "they speak at full speed, and never in English"),
        (0.61f, "Giving you a reason to be there", "three small things to get done before you leave"),
        (0.79f, "Go.", "walk up to anyone and press E to talk"),
    };
    // Fractions of the trip: the globe rushes up to the pin; the clouds close over it.
    const float ZoomFrom = 0.11f, ZoomTo = 0.34f, CoverFrom = 0.11f, CoverTo = 0.64f;

    enum Phase { Writing, Boarding, Departing }

    Phase phase = Phase.Writing;
    float phaseStart;
    string line = "";
    bool loading;

    // The globe.
    Transform globe;
    Transform pin;
    float yaw, yawTarget;
    bool hasPin;
    string placeName = "";
    float pinLat, pinLon;

    Material skyMaterial, litMaterial, cloudMaterial;
    readonly List<Mesh> meshes = new();        // everything built here, for cleanup
    readonly List<Mesh> cloudMeshes = new();   // the few cloud shapes the puffs pick from
    Light sun;

    // The clouds: a sea of them below the globe; on Enter they all stream past. Nothing
    // drifts above the horizon: a small distant cloud reads as a second globe.
    readonly List<Puff> puffs = new();
    System.Random cloudRng = new(11);

    class Puff
    {
        public Transform t;
        public float drift;   // idle speed along x
        public float size;
    }
    Camera cam;
    CloudCurtain curtain;

    // Type, laid out on a 1280×720 canvas and scaled to the window.
    const float DesignW = 1280f, DesignH = 720f;
    Font sans;
    GUIStyle wordmark, tagline, sentence, blank, placeholder, hint, status, statusSub, pinLabel;

    string Line => string.IsNullOrWhiteSpace(line) ? example : line.Trim();
    float Since => Time.time - phaseStart;
    /// <summary>Progress through the trip, 0–1; 0 while writing.</summary>
    float Trip => phase == Phase.Writing ? 0f : phase == Phase.Departing ? 1f : Mathf.Clamp01(Since / tripSeconds);

    void Start()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        cam = Camera.main;

        Sky();
        BuildGlobe();
        BuildClouds();
        yaw = yawTarget = 40f;

        curtain = CloudCurtain.Get();
        curtain.Arrive(arriveSeconds);
    }

    void OnDestroy()
    {
        foreach (var m in new[] { skyMaterial, litMaterial, cloudMaterial })
            if (m != null)
                Destroy(m);
        foreach (var m in meshes)
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
        float cover = phase == Phase.Writing ? 0f : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(CoverFrom, CoverTo, t));
        MoveClouds(cover);
        if (curtain != null && phase != Phase.Departing)
            curtain.Cover = cover;
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
        PlayerPrefs.Save();
        Debug.Log($"Wish: «{Line}»");
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

    // ---- the sky, the globe and the clouds ------------------------------------------

    void Sky()
    {
        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = skyHorizon;
        }
        // A dome around the camera with a vertical gradient: pale at the horizon, blue above.
        var dome = new GameObject("Sky");
        dome.transform.SetParent(transform, false);
        dome.transform.position = cam != null ? cam.transform.position : Vector3.zero;
        dome.transform.localScale = Vector3.one * 60f;
        skyMaterial = LowPoly.GradientMaterial(skyBelow, skyHorizon, skyZenith);
        Piece(dome, LowPoly.Dome(), skyMaterial);

        // Daylight from up and to the right, and a soft blue fill so the night side stays pastel.
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.55f, 0.62f, 0.76f);
        sun = new GameObject("Sun").AddComponent<Light>();
        sun.transform.SetParent(transform, false);
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.97f, 0.92f);
        sun.intensity = 1.4f;
        sun.shadows = LightShadows.None;
        sun.transform.rotation = Quaternion.Euler(30f, -40f, 0f);
    }

    void BuildGlobe()
    {
        globe = new GameObject("Globe").transform;
        globe.SetParent(transform, false);
        globe.position = globeCentre;

        litMaterial = LowPoly.PaletteMaterial(true);
        cloudMaterial = LowPoly.PaletteMaterial(false);

        var map = Resources.Load<Texture2D>("Wish/earth_map");
        if (map == null)
            Debug.LogWarning("Wish: no land/sea map at Resources/Wish/earth_map; the Earth will be all ocean");
        var earth = new GameObject("Earth");
        earth.transform.SetParent(globe, false);
        earth.transform.localScale = Vector3.one * globeRadius;
        Piece(earth, LowPoly.Earth(map), litMaterial);
    }

    void BuildClouds()
    {
        for (int i = 0; i < 5; i++)
            cloudMeshes.Add(LowPoly.Cloud(100 + i));
        for (int i = 0; i < 34; i++)
            Spawn(true);
    }

    float R(float lo, float hi) => lo + (float)cloudRng.NextDouble() * (hi - lo);

    /// <summary>A cloud in the sea below; <paramref name="anywhere"/> for the initial scatter, else far ahead.</summary>
    Puff Spawn(bool anywhere)
    {
        var go = new GameObject("Cloud");
        go.transform.SetParent(transform, false);
        go.transform.rotation = Quaternion.Euler(0f, R(-40f, 40f), 0f);
        var puff = new Puff { t = go.transform, drift = R(0.10f, 0.22f), size = R(0.9f, 1.7f) };
        go.transform.localScale = Vector3.one * puff.size;
        var mr = Piece(go, cloudMeshes[cloudRng.Next(cloudMeshes.Count)], cloudMaterial);
        mr.shadowCastingMode = ShadowCastingMode.Off;
        Reset(puff, anywhere);
        puffs.Add(puff);
        return puff;
    }

    void Reset(Puff p, bool anywhere)
    {
        float z = anywhere ? R(-3f, 15f) : R(14f, 19f);
        p.t.position = new Vector3(R(-10f, 10f), R(-2.7f, -1.8f), z);
    }

    /// <summary>Idle: everything drifts. Boarding: everything streams past, converging on the camera.</summary>
    void MoveClouds(float cover)
    {
        float dt = Time.deltaTime;
        float camZ = cam != null ? cam.transform.position.z : -5f;
        float rush = cover * cover * 11f;                       // forward speed
        // The whole sea climbs to meet you; move it by the change so each cloud keeps its own row.
        float lift = Mathf.SmoothStep(0f, 1f, cover) * 2.4f;
        float dLift = lift - seaLift;
        seaLift = lift;
        foreach (var p in puffs)
        {
            var pos = p.t.position;
            pos.x += p.drift * dt;
            pos.z -= rush * dt;
            pos.y += dLift;
            if (pos.x > 11f)
                pos.x -= 22f;
            if (pos.z < camZ - 2.5f)
            {
                // Gone past: come round again far ahead, gathering round the flight path the deeper in you are.
                Reset(p, false);
                pos = p.t.position;
                pos.x = Mathf.Lerp(pos.x, R(-2.5f, 2.5f), cover);
                pos.y = Mathf.Lerp(pos.y + seaLift, R(-1.5f, 1.8f), cover);
            }
            p.t.position = pos;
        }
        // More clouds the deeper in you are, so the view fills before the wash.
        int want = 34 + Mathf.RoundToInt(Mathf.InverseLerp(0.3f, 0.9f, cover) * 30f);
        while (puffs.Count < want)
        {
            var extra = Spawn(false);
            extra.t.position = new Vector3(R(-3f, 3f), R(-1.5f, 2f), extra.t.position.z);
        }
    }

    float seaLift;

    /// <summary>Give a GameObject a mesh and a material; remembers the mesh for cleanup.</summary>
    MeshRenderer Piece(GameObject go, Mesh mesh, Material material)
    {
        if (!meshes.Contains(mesh))
            meshes.Add(mesh);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = material;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        return mr;
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
        var marker = new GameObject("Pin");
        Piece(marker, LowPoly.Ball(LowPoly.Swatch.Cloud), cloudMaterial);   // white, unlit, like the clouds
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
        }
        else if (phase == Phase.Boarding && e.keyCode == KeyCode.Escape)
        {
            ChangeMind();
            e.Use();
        }
    }

    void Wordmark(float w)
    {
        Text(new Rect(0f, 54f, w, 84f), "Scenar.io", wordmark, ink, TextAnchor.MiddleCenter);
        Text(new Rect(0f, 138f, w, 24f), "Learn languages like a local.", tagline, new Color(ink.r, ink.g, ink.b, 0.6f), TextAnchor.MiddleCenter);
    }

    void Sentence(float alpha)
    {
        if (alpha <= 0f)
            return;
        const float x = 96f, width = 600f;
        float y = 262f;
        var dark = new Color(ink.r, ink.g, ink.b, alpha);
        var faint = new Color(ink.r, ink.g, ink.b, 0.42f * alpha);

        Text(new Rect(x, y, width, 46f), "Put me in", sentence, dark);
        y += 60f;

        // The blank: typed straight onto the sky, with a rule under it.
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
        GUI.color = dark;
        GUI.DrawTexture(new Rect(x, y + 50f, width, 2f), Texture2D.whiteTexture);
        GUI.color = Color.white;

        y += 82f;
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
        Ink(style, color);
        style.alignment = anchor;
        GUI.Label(r, s, style);
    }

    /// <summary>One colour in every state: the default skin turns type white on hover.</summary>
    static void Ink(GUIStyle style, Color color)
    {
        foreach (var state in new[] { style.normal, style.hover, style.active, style.focused, style.onNormal, style.onHover, style.onActive, style.onFocused })
            state.textColor = color;
    }

    void EnsureStyles()
    {
        if (wordmark != null)
            return;
        sans = PickFont(16);

        GUIStyle Plain(int size, FontStyle weight = FontStyle.Normal)
        {
            var s = new GUIStyle(GUI.skin.label) { font = sans, fontSize = size, fontStyle = weight, richText = false, wordWrap = false, padding = new RectOffset(0, 0, 0, 0) };
            Ink(s, ink);
            return s;
        }

        wordmark = Plain(76, FontStyle.Bold);
        tagline = Plain(17);
        sentence = Plain(36);
        placeholder = Plain(36);
        hint = Plain(16);
        status = Plain(44);
        statusSub = Plain(18);
        pinLabel = Plain(14, FontStyle.Bold);

        // Built from the label, not the text field, so it draws no box: just the type and a caret.
        blank = Plain(36);
        foreach (var state in new[] { blank.normal, blank.hover, blank.active, blank.focused, blank.onNormal, blank.onHover, blank.onActive, blank.onFocused })
        {
            state.background = null;
#if UNITY_EDITOR
            state.scaledBackgrounds = Array.Empty<Texture2D>(); // editor-only API; the player strips it
#endif
        }
        GUI.skin.settings.cursorColor = ink;
        GUI.skin.settings.cursorFlashSpeed = 1.1f;
        GUI.skin.settings.selectionColor = new Color(ink.r, ink.g, ink.b, 0.2f);
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
