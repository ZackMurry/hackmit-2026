using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// <summary>
/// Plays looping idle mocap on the avatar so she stands naturally instead of in
/// the FBX bind (T) pose. Cycles through the clips with crossfades so the idle
/// never looks like a single repeating loop.
///
/// Clips are Microsoft Rocketbox mocap for the same Biped skeleton, so they bind
/// directly by bone path; no Humanoid avatar or AnimatorController needed.
/// </summary>
[RequireComponent(typeof(NpcAvatarLoader))]
public class NpcIdleAnimation : MonoBehaviour
{
    [Tooltip("Idle clips. Overrides Clip Resources if non-empty.")]
    public AnimationClip[] clips;

    [Tooltip("Fallback: model paths under a Resources folder whose animation clips to play.")]
    public string[] clipResources =
    {
        "Avatars/Animations/f_idle_neutral_01",
        "Avatars/Animations/f_idle_neutral_02",
        "Avatars/Animations/f_idle_breathe_01",
    };

    [Tooltip("Seconds to crossfade between idles.")]
    public float blendTime = 0.6f;

    [Tooltip("Play each clip this many loops before picking another (min, max).")]
    public Vector2Int loopsPerClip = new(1, 2);

    [Header("Walking")]
    [Tooltip("Optional walk cycle. Overrides Walk Clip Resource if set.")]
    public AnimationClip walkClip;
    [Tooltip("Fallback: Resources path of a walk cycle for the same skeleton. Leave empty to let " +
             "NpcWalker animate the legs procedurally.")]
    public string walkClipResource = "";

    /// <summary>True once the graph is driving the skeleton.</summary>
    public bool IsPlaying => graph.IsValid() && graph.IsPlaying();

    /// <summary>True when a walk clip is wired into the mixer.</summary>
    public bool HasWalkClip => walkIndex >= 0;

    /// <summary>0 = idle, 1 = walk clip. Set by <see cref="NpcWalker"/> each frame.</summary>
    public float WalkWeight { get; set; }

    readonly List<AnimationClip> loaded = new();
    int walkIndex = -1;       // mixer input of the walk clip, -1 if none
    float appliedWalk = -1f;  // last WalkWeight pushed to the mixer
    PlayableGraph graph;
    AnimationMixerPlayable mixer;
    AnimationClipPlayable[] players;
    int current = -1;
    int previous = -1;
    float blend = 1f;         // 0 = fully previous, 1 = fully current
    float switchAt;           // clip-time at which to move on

    void Awake()
    {
        GetComponent<NpcAvatarLoader>().Loaded += OnAvatarLoaded;
    }

    void OnAvatarLoaded(Transform avatar)
    {
        loaded.Clear();
        if (clips != null && clips.Length > 0)
            loaded.AddRange(clips);
        else
            foreach (string path in clipResources)
            {
                var found = Resources.LoadAll<AnimationClip>(path);
                if (found.Length == 0)
                    Debug.LogWarning($"NpcIdleAnimation: no AnimationClip at Resources/{path}");
                loaded.AddRange(found);
            }
        // FBX imports also carry an editor-only "__preview__" clip.
        loaded.RemoveAll(c => c == null || c.name.StartsWith("__preview__"));

        if (loaded.Count == 0)
        {
            Debug.LogWarning("NpcIdleAnimation: no idle clips found; avatar will stay in bind pose.");
            return;
        }

        // Generic FBX imports already put an Animator on the root; reuse it.
        var animator = avatar.GetComponent<Animator>();
        if (animator == null)
            animator = avatar.gameObject.AddComponent<Animator>();
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.enabled = true;

        Debug.Log($"NpcIdleAnimation: {loaded.Count} clip(s) [{string.Join(", ", loaded.ConvertAll(c => $"{c.name} {c.length:0.0}s"))}] " +
                  $"on '{avatar.name}' (avatar={(animator.avatar ? animator.avatar.name : "none")}, " +
                  $"children: {string.Join(", ", ChildNames(avatar))})");
#if UNITY_EDITOR
        // Show what the first clip wants to drive so a path mismatch is obvious.
        var bindings = UnityEditor.AnimationUtility.GetCurveBindings(loaded[0]);
        var paths = new HashSet<string>();
        foreach (var b in bindings) paths.Add(b.path);
        int missing = 0;
        foreach (var p in paths) if (avatar.Find(p) == null) missing++;
        Debug.Log($"NpcIdleAnimation: '{loaded[0].name}' has {bindings.Length} curves on {paths.Count} transforms, " +
                  $"{missing} not found under avatar. First: {string.Join(" | ", System.Linq.Enumerable.Take(paths, 3))}");
#endif
        StartCoroutine(VerifyPoseChanged(avatar));

        var walk = LoadWalkClip();
        int inputs = loaded.Count + (walk != null ? 1 : 0);

        graph = PlayableGraph.Create($"{name} idle");
        graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
        mixer = AnimationMixerPlayable.Create(graph, inputs);
        players = new AnimationClipPlayable[inputs];
        for (int i = 0; i < inputs; i++)
        {
            players[i] = AnimationClipPlayable.Create(graph, i < loaded.Count ? loaded[i] : walk);
            players[i].SetApplyFootIK(false);
            players[i].SetApplyPlayableIK(false);
            graph.Connect(players[i], 0, mixer, i);
            mixer.SetInputWeight(i, 0f);
        }
        walkIndex = walk != null ? loaded.Count : -1;

        var output = AnimationPlayableOutput.Create(graph, "Idle", animator);
        output.SetSourcePlayable(mixer);

        StartClip(0);
        mixer.SetInputWeight(0, 1f);
        blend = 1f;
        graph.Play();
    }

    void StartClip(int index)
    {
        previous = current;
        current = index;
        players[current].SetTime(0);
        int loops = Random.Range(loopsPerClip.x, loopsPerClip.y + 1);
        switchAt = Mathf.Max(1, loops) * loaded[current].length - blendTime;
        if (previous >= 0)
            blend = 0f;
    }

    void Update()
    {
        if (!IsPlaying)
            return;

        float walkWeight = walkIndex >= 0 ? Mathf.Clamp01(WalkWeight) : 0f;
        if (blend < 1f || walkWeight != appliedWalk)
        {
            blend = Mathf.MoveTowards(blend, 1f, Time.deltaTime / Mathf.Max(0.01f, blendTime));
            float idle = 1f - walkWeight;
            for (int i = 0; i < loaded.Count; i++)
                mixer.SetInputWeight(i, idle * (i == current ? blend : i == previous ? 1f - blend : 0f));
            if (walkIndex >= 0)
                mixer.SetInputWeight(walkIndex, walkWeight);
            appliedWalk = walkWeight;
        }

        if (loaded.Count > 1 && players[current].GetTime() >= switchAt)
        {
            int next = Random.Range(0, loaded.Count - 1);
            if (next >= current)
                next++; // never repeat the same clip back to back
            StartClip(next);
        }
    }

    AnimationClip LoadWalkClip()
    {
        if (walkClip != null)
            return walkClip;
        if (string.IsNullOrEmpty(walkClipResource))
            return null;
        foreach (var clip in Resources.LoadAll<AnimationClip>(walkClipResource))
            if (clip != null && !clip.name.StartsWith("__preview__"))
                return clip;
        Debug.LogWarning($"NpcIdleAnimation: no AnimationClip at Resources/{walkClipResource}; using procedural walk.");
        return null;
    }

    static IEnumerable<string> ChildNames(Transform t)
    {
        for (int i = 0; i < t.childCount; i++)
            yield return t.GetChild(i).name;
    }

    /// <summary>Confirms the clip actually moved the skeleton; otherwise says why it might not have.</summary>
    IEnumerator VerifyPoseChanged(Transform avatar)
    {
        var arm = avatar.Find("Bip01/Bip01 Pelvis/Bip01 Spine/Bip01 Spine1/Bip01 Spine2/Bip01 Neck/Bip01 L Clavicle/Bip01 L UpperArm");
        if (arm == null)
        {
            Debug.LogWarning("NpcIdleAnimation: left upper arm not at the expected Biped path; hierarchy differs from the clips.");
            yield break;
        }
        var before = arm.localRotation;
        yield return new WaitForSeconds(0.5f);
        if (Quaternion.Angle(before, arm.localRotation) < 0.5f)
            Debug.LogError("NpcIdleAnimation: graph is playing but bones did not move. " +
                           "Check the clips imported as Generic (Rig tab) and that no AnimatorController is on the avatar.");
        else
            Debug.Log("NpcIdleAnimation: idle mocap is driving the skeleton.");
    }

    void OnDestroy()
    {
        if (graph.IsValid())
            graph.Destroy();
    }
}
