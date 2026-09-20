using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Clouds drawn over everything, that outlive the scene they were made in.
///
/// Two drifting layers of wisps and a bank sitting along the bottom of the screen, all
/// procedural (tileable fBm noise made once at start) and drawn with IMGUI so they sit
/// on top of any camera. <see cref="Cover"/> runs from 0 — a few clouds about — to 1 —
/// inside one, mostly white: the layers thicken and grow, the bank climbs, and a white
/// wash comes up over the lot. The object is DontDestroyOnLoad, so the front can bring
/// the cover to 1, load the destination behind it, and the clouds part onto the spawn
/// (<see cref="Reveal"/>); or a scene can open through them (<see cref="Arrive"/>).
/// </summary>
public class CloudCurtain : MonoBehaviour
{
    public static CloudCurtain Current { get; private set; }

    /// <summary>The curtain, made if there isn't one.</summary>
    public static CloudCurtain Get()
    {
        if (Current == null)
        {
            var go = new GameObject("CloudCurtain");
            DontDestroyOnLoad(go);
            Current = go.AddComponent<CloudCurtain>();
        }
        return Current;
    }

    /// <summary>Where the clouds are asked to be: 0 a few about, 1 inside one. Eased toward.</summary>
    public float Cover { get => target; set => target = Mathf.Clamp01(value); }

    enum Mode { Steady, Arriving, Revealing }

    Mode mode = Mode.Steady;
    float target, cover;
    float ambient = 1f;          // how present the idle clouds are; goes to 0 as the curtain parts for good
    float modeStart, modeSeconds;
    bool sceneLanded;
    float drift;                 // the layers slide apart as the curtain parts

    Texture2D wispA, wispB, bank;
    float scrollA, scrollB, scrollBank;

    void Awake()
    {
        // Small and bilinear: clouds are soft, and this runs once at start.
        wispA = Tileable(256, 256, 4.0f, 1, false);
        wispB = Tileable(256, 256, 3.0f, 2, false);
        bank = Tileable(512, 128, 3.2f, 3, true);
    }

    void OnDestroy()
    {
        if (Current == this)
            Current = null;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        foreach (var t in new[] { wispA, wispB, bank })
            if (t != null)
                Destroy(t);
    }

    /// <summary>Start inside a cloud and open onto the current scene, keeping the idle clouds.</summary>
    public void Arrive(float seconds)
    {
        cover = 1f;
        target = 0f;
        ambient = 1f;
        mode = Mode.Arriving;
        modeStart = Time.unscaledTime;
        modeSeconds = seconds;
    }

    /// <summary>
    /// Hold white through the coming scene load, then part over <paramref name="seconds"/>
    /// onto whatever loaded, and go away.
    /// </summary>
    public void Reveal(float seconds)
    {
        cover = target = 1f;
        mode = Mode.Revealing;
        modeSeconds = seconds;
        sceneLanded = false;
        modeStart = Time.unscaledTime;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode loadMode)
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        Land();
    }

    void Land()
    {
        sceneLanded = true;
        modeStart = Time.unscaledTime + 0.4f; // a beat of white while the new scene settles
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        switch (mode)
        {
            case Mode.Steady:
                cover = Mathf.Lerp(cover, target, 1f - Mathf.Exp(-dt / 0.35f));
                break;
            case Mode.Arriving:
            {
                float p = Mathf.Clamp01((Time.unscaledTime - modeStart) / modeSeconds);
                cover = 1f - Mathf.SmoothStep(0f, 1f, p);
                if (p >= 1f)
                    mode = Mode.Steady;
                break;
            }
            case Mode.Revealing:
            {
                if (!sceneLanded)
                {
                    if (Time.unscaledTime - modeStart > 4f)
                        Land();            // the load never told us; don't stay white forever
                    cover = 1f;
                    break;
                }
                float p = Mathf.Clamp01((Time.unscaledTime - modeStart) / modeSeconds);
                float e = Mathf.SmoothStep(0f, 1f, p);
                cover = 1f - e;
                ambient = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 1f, p));
                drift += dt * e * 0.35f;
                if (p >= 1f)
                    Destroy(gameObject);
                break;
            }
        }
        float rush = 1f + 3f * cover;     // the clouds stream past faster the deeper in you are
        scrollA += dt * 0.020f * rush;
        scrollB -= dt * 0.014f * rush;
        scrollBank += dt * 0.010f * rush;
    }

    // ---- drawing --------------------------------------------------------------------

    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint)
            return;
        GUI.depth = -100;                 // over every scene's own UI
        var prev = GUI.color;
        float w = Screen.width, h = Screen.height, c = cover, a = ambient;
        int passes = 1 + Mathf.RoundToInt(c * 2.5f);

        // Wisps: two layers at different scales and speeds; nearer and denser as cover rises,
        // and sliding apart from each other as the curtain parts.
        Layer(wispA, new Rect(0f, 0f, w, h), Mathf.Lerp(1.6f, 0.9f, c), new Vector2(scrollA - drift, 0.13f),
              Mathf.Lerp(0.55f * a, 1f, c), passes);
        Layer(wispB, new Rect(0f, 0f, w, h), Mathf.Lerp(1.1f, 0.6f, c), new Vector2(scrollB + drift, 0.61f),
              Mathf.Lerp(0.50f * a, 1f, c), passes);

        // The bank along the bottom, climbing to swallow the screen.
        float bankH = h * Mathf.Lerp(0.5f, 1.7f, c);
        var bankRect = new Rect(0f, h - bankH, w, bankH);
        float bankTiles = (w / bankH) / 4f; // the texture is 4:1
        GUI.color = new Color(1f, 1f, 1f, Mathf.Lerp(a, 1f, c));
        GUI.DrawTextureWithTexCoords(bankRect, bank, new Rect(scrollBank, 0f, bankTiles, 1f));

        // The white wash.
        float wash = Mathf.Pow(Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.4f, 1f, c)), 1.1f) * 0.94f;
        if (wash > 0f)
        {
            GUI.color = new Color(1f, 1f, 1f, wash);
            GUI.DrawTexture(new Rect(0f, 0f, w, h), Texture2D.whiteTexture);
        }
        GUI.color = prev;
    }

    /// <summary>Tile a cloud texture across <paramref name="rect"/>, <paramref name="tiles"/> across, drawn <paramref name="passes"/> times to thicken.</summary>
    static void Layer(Texture2D tex, Rect rect, float tiles, Vector2 offset, float alpha, int passes)
    {
        if (alpha <= 0f)
            return;
        GUI.color = new Color(1f, 1f, 1f, alpha);
        var coords = new Rect(offset.x, offset.y, tiles, tiles * rect.height / rect.width);
        for (int i = 0; i < passes; i++)
            GUI.DrawTextureWithTexCoords(rect, tex, coords);
    }

    // ---- the noise ------------------------------------------------------------------

    /// <summary>
    /// White with cloud-shaped alpha, wrapping seamlessly across (and, unless it is the
    /// bank, down). The bank is opaque along its bottom edge and breaks into puffs above.
    /// </summary>
    static Texture2D Tileable(int w, int h, float scale, int seed, bool isBank)
    {
        var raw = new float[w * h];
        float ox = seed * 101.7f, oy = seed * 37.3f;
        double sum = 0, sumSq = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = x / (float)w, v = y / (float)h;
                // Blend copies offset by one tile so the edges meet.
                float n = isBank
                    ? Fbm(u, v, ox, oy, scale) * (1f - u) + Fbm(u - 1f, v, ox, oy, scale) * u
                    : Fbm(u, v, ox, oy, scale) * (1f - u) * (1f - v) + Fbm(u - 1f, v, ox, oy, scale) * u * (1f - v)
                    + Fbm(u, v - 1f, ox, oy, scale) * (1f - u) * v + Fbm(u - 1f, v - 1f, ox, oy, scale) * u * v;
                raw[y * w + x] = n;
                sum += n;
                sumSq += n * n;
            }
        // Normalise so the thresholds below mean the same whatever the noise's spread.
        float mean = (float)(sum / raw.Length);
        float sd = Mathf.Max(1e-4f, (float)System.Math.Sqrt(sumSq / raw.Length - mean * mean));

        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };
        if (isBank)
            tex.wrapModeV = TextureWrapMode.Clamp;
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float n = 0.5f + (raw[y * w + x] - mean) / sd * 0.18f;
                float d;
                if (isBank)
                {
                    float bottom = 1f - y / (float)h;            // row 0 is the bottom of a Unity texture
                    d = Smooth(0.42f, 0.70f, n + bottom * 0.55f - 0.22f);
                }
                else
                    d = Smooth(0.53f, 0.79f, n);
                px[y * w + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(d * 255f));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return tex;
    }

    static float Fbm(float u, float v, float ox, float oy, float scale)
    {
        float s = 0f, amp = 1f, total = 0f, f = 1f;
        for (int o = 0; o < 5; o++)
        {
            s += amp * Mathf.PerlinNoise(ox + u * scale * f + 13.1f * o, oy + v * scale * f + 7.7f * o);
            total += amp;
            amp *= 0.5f;
            f *= 2f;
        }
        return s / total;
    }

    static float Smooth(float a, float b, float x)
    {
        float t = Mathf.Clamp01((x - a) / (b - a));
        return t * t * (3f - 2f * t);
    }
}
