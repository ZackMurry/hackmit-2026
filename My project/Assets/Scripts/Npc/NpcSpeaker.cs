using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Plays speech audio from the NPC's mouth and drives the avatar's mouth
/// blendshapes from the audio amplitude. The server's reply audio arrives as an
/// AudioClip via <see cref="Speak"/>; lines without audio (greetings, offline
/// canned replies) play the prerecorded sample instead.
/// </summary>
[RequireComponent(typeof(NpcAvatarLoader))]
public class NpcSpeaker : MonoBehaviour
{
    [Header("Lip-sync")]
    [Tooltip("Blendshape name suffixes to drive (case-insensitive), first one found on the " +
             "avatar wins. Matches Rocketbox 'AA_VI_10_aa' / 'AK_25_JawOpen' as well as " +
             "Ready Player Me 'viseme_aa' / 'jawOpen'.")]
    public string[] mouthShapes = { "_aa", "jawopen", "mouthopen" };
    [Tooltip("Amplitude to blendshape weight multiplier.")]
    public float mouthGain = 400f;
    public float mouthSmoothing = 20f;

    [Header("Audio")]
    [Tooltip("Bone the AudioSource is attached to so speech comes from the mouth.")]
    public string mouthBone = "Head";
    public float volume = 1f;

    [Header("Placeholder voice")]
    [Tooltip("Played for lines that have no server audio. Empty = load placeholderResource.")]
    public AudioClip placeholderClip;
    [Tooltip("Resources path of a prerecorded sample, e.g. Audio/sample_es. Empty (the default) = a " +
             "synthetic murmur, because that sample is a man's voice and it came out of Maria's mouth " +
             "whenever the speech provider was down.")]
    public string placeholderResource = "";

    public bool IsSpeaking => source != null && source.isPlaying;

    /// <summary>Fired when a clip passed to Speak finishes.</summary>
    public event Action FinishedSpeaking;

    static readonly List<NpcSpeaker> everyone = new();

    /// <summary>Whoever is talking right now, or null. Only one NPC has the floor at a time.</summary>
    public static NpcSpeaker Talking
    {
        get
        {
            foreach (var s in everyone)
                if (s.IsSpeaking)
                    return s;
            return null;
        }
    }

    /// <summary>Someone other than <paramref name="me"/> is mid-line.</summary>
    public static bool SomeoneElseSpeaking(NpcSpeaker me)
    {
        var talking = Talking;
        return talking != null && talking != me;
    }

    AudioSource source;
    readonly List<(SkinnedMeshRenderer smr, int index)> mouthTargets = new();
    readonly float[] samples = new float[256];
    float mouthWeight;
    bool wasSpeaking;

    void OnEnable() => everyone.Add(this);
    void OnDisable() => everyone.Remove(this);

    void Awake()
    {
        GetComponent<NpcAvatarLoader>().Loaded += OnAvatarLoaded;

        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 1f;
        source.minDistance = 1f;
        source.maxDistance = 15f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.volume = volume;
    }

    void OnAvatarLoaded(Transform avatar)
    {
        mouthTargets.Clear();
        var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>();
        foreach (string shape in mouthShapes)
        {
            foreach (var smr in renderers)
            {
                var mesh = smr.sharedMesh;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    if (mesh.GetBlendShapeName(i).EndsWith(shape, StringComparison.OrdinalIgnoreCase))
                    {
                        mouthTargets.Add((smr, i));
                        break;
                    }
                }
            }
            if (mouthTargets.Count > 0)
                break;
        }

        if (mouthTargets.Count == 0)
            Debug.LogWarning("NpcSpeaker: no mouth blendshape found on avatar; lip-sync disabled.");

        // Reparent the source to the head so the voice is positioned at the mouth.
        var mouth = GetComponent<NpcAvatarLoader>().FindBone(mouthBone);
        if (mouth != null)
        {
            var moved = mouth.gameObject.AddComponent<AudioSource>();
            CopySettings(source, moved);
            Destroy(source);
            source = moved;
        }
    }

    /// <summary>Play a speech clip. Replaces anything currently playing.</summary>
    public void Speak(AudioClip clip)
    {
        source.Stop();
        source.clip = clip;
        source.Play();
    }

    public void Stop() => source.Stop();

    /// <summary>
    /// Speaks the prerecorded Spanish sample (or a synthetic "blah blah" of
    /// <paramref name="seconds"/> if the sample is missing) for lines that have
    /// no server audio, so lip-sync and positioning can be tested.
    /// </summary>
    public void SpeakTest(float seconds = 2.5f)
    {
        var clip = Placeholder();
        Speak(clip != null ? clip : MakeTestClip(seconds));
    }

    AudioClip Placeholder()
    {
        if (placeholderClip == null && !string.IsNullOrEmpty(placeholderResource))
        {
            placeholderClip = Resources.Load<AudioClip>(placeholderResource);
            if (placeholderClip == null)
            {
                Debug.LogWarning($"NpcSpeaker: no AudioClip at Resources/{placeholderResource}; using synthetic voice.");
                placeholderResource = ""; // don't retry every line
            }
        }
        return placeholderClip;
    }

    void Update()
    {
        bool speaking = IsSpeaking;

        float target = 0f;
        if (speaking)
        {
            source.GetOutputData(samples, 0);
            float sum = 0f;
            foreach (float s in samples)
                sum += s * s;
            target = Mathf.Clamp(Mathf.Sqrt(sum / samples.Length) * mouthGain, 0f, 100f);
        }

        mouthWeight = Mathf.Lerp(mouthWeight, target, mouthSmoothing * Time.deltaTime);
        foreach (var (smr, index) in mouthTargets)
            smr.SetBlendShapeWeight(index, mouthWeight);

        if (wasSpeaking && !speaking)
            FinishedSpeaking?.Invoke();
        wasSpeaking = speaking;
    }

    static void CopySettings(AudioSource from, AudioSource to)
    {
        to.playOnAwake = false;
        to.spatialBlend = from.spatialBlend;
        to.minDistance = from.minDistance;
        to.maxDistance = from.maxDistance;
        to.rolloffMode = from.rolloffMode;
        to.volume = from.volume;
    }

    static AudioClip MakeTestClip(float seconds)
    {
        const int rate = 44100;
        int count = Mathf.CeilToInt(seconds * rate);
        var data = new float[count];
        var rng = new System.Random(1);

        // Voice-ish buzz: a low fundamental with harmonics, chopped into
        // syllables by a slow envelope so the mouth visibly opens and closes.
        float pitch = 150f;
        for (int i = 0; i < count; i++)
        {
            float t = (float)i / rate;
            if (i % (rate / 6) == 0) // new "syllable" every ~170 ms
                pitch = 130f + (float)rng.NextDouble() * 60f;

            float env = Mathf.Max(0f, Mathf.Sin(t * 6f * Mathf.PI)); // ~3 syllables/s
            float v = Mathf.Sin(2f * Mathf.PI * pitch * t)
                    + 0.5f * Mathf.Sin(2f * Mathf.PI * pitch * 2f * t)
                    + 0.25f * Mathf.Sin(2f * Mathf.PI * pitch * 3f * t);
            data[i] = v * env * 0.15f;
        }

        var clip = AudioClip.Create("NpcTestSpeech", count, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
