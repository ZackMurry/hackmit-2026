using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Plays speech audio from the NPC's mouth and drives the avatar's mouth
/// blendshapes from the audio amplitude. This is the ElevenLabs integration
/// point: turn the TTS response into an AudioClip and call <see cref="Speak"/>.
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

    public bool IsSpeaking => source != null && source.isPlaying;

    /// <summary>Fired when a clip passed to Speak finishes.</summary>
    public event Action FinishedSpeaking;

    AudioSource source;
    readonly List<(SkinnedMeshRenderer smr, int index)> mouthTargets = new();
    readonly float[] samples = new float[256];
    float mouthWeight;
    bool wasSpeaking;

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
    /// Speaks a synthetic "blah blah" so you can test lip-sync and positioning
    /// before ElevenLabs is wired up.
    /// </summary>
    public void SpeakTest(float seconds = 2.5f)
    {
        Speak(MakeTestClip(seconds));
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
