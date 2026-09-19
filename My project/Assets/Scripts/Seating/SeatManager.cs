using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Places one <see cref="Seat"/> per entry of StreamingAssets/seats.json and
/// re-reads the file when it changes, like <see cref="NpcManager"/>. Seats put
/// in the scene by hand register too.
///
/// <code>
/// { "seats": [
///   { "id": "table_a", "position": {"x": 1.1, "y": -0.9, "z": 8.7}, "yaw": 90, "playerCanSit": true }
/// ] }
/// </code>
/// position = point on the cushion (world), yaw = direction the sitter faces (0 = +Z).
/// </summary>
public class SeatManager : MonoBehaviour
{
    public static SeatManager Instance { get; private set; }

    [Tooltip("JSON file under StreamingAssets.")]
    public string fileName = "seats.json";
    [Tooltip("Rebuild the seats when the file changes on disk.")]
    public bool watchFile = true;

#pragma warning disable 0649 // DTO fields are filled by JsonUtility
    [Serializable]
    public class SeatDefinition
    {
        public string id;
        public Vector3 position;
        public float yaw;
        public bool playerCanSit = true;
        public float heightAboveFloor = 0.6f;
    }

    [Serializable]
    public class SeatList
    {
        public SeatDefinition[] seats = Array.Empty<SeatDefinition>();
    }
#pragma warning restore 0649

    /// <summary>Every live seat, JSON-spawned or hand-placed.</summary>
    public static IReadOnlyList<Seat> All => all;
    static readonly List<Seat> all = new();

    /// <summary>Fired after the JSON seats are (re)built.</summary>
    public event Action Changed;

    readonly List<GameObject> spawned = new();
    string FilePath => Path.Combine(Application.streamingAssetsPath, fileName);
    DateTime lastWrite;
    float nextPoll;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void Start()
    {
        Load();
    }

    void Update()
    {
        if (!watchFile || Time.unscaledTime < nextPoll)
            return;
        nextPoll = Time.unscaledTime + 0.5f;
        if (File.Exists(FilePath) && File.GetLastWriteTimeUtc(FilePath) != lastWrite)
            Load();
    }

    public void Load()
    {
        if (!File.Exists(FilePath))
        {
            Debug.Log($"SeatManager: {FilePath} not found; no seats.");
            return;
        }

        SeatList list;
        try
        {
            list = JsonUtility.FromJson<SeatList>(File.ReadAllText(FilePath)) ?? new SeatList();
            lastWrite = File.GetLastWriteTimeUtc(FilePath);
        }
        catch (Exception e)
        {
            Debug.LogError($"SeatManager: could not parse {FilePath}: {e.Message}");
            return;
        }

        foreach (var go in spawned)
            if (go != null)
                Destroy(go);
        spawned.Clear();
        all.RemoveAll(s => s == null);

        foreach (var def in list.seats)
        {
            if (def == null)
                continue;
            var go = new GameObject($"Seat {def.id}");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.SetPositionAndRotation(def.position, Quaternion.Euler(0f, def.yaw, 0f));
            var seat = go.AddComponent<Seat>();
            seat.id = def.id ?? "";
            seat.playerCanSit = def.playerCanSit;
            seat.heightAboveFloor = def.heightAboveFloor;
            Register(seat);
            spawned.Add(go);
        }
        Debug.Log($"SeatManager: {spawned.Count} seat(s) from {fileName}");
        Changed?.Invoke();
    }

    public static void Register(Seat seat)
    {
        if (seat != null && !all.Contains(seat))
            all.Add(seat);
    }

    public static void Unregister(Seat seat) => all.Remove(seat);

    /// <summary>Seat by id, or null (also finds hand-placed seats).</summary>
    public static Seat Find(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        all.RemoveAll(s => s == null);
        foreach (var seat in all)
            if (seat.id == id)
                return seat;
        return null;
    }

    /// <summary>Closest seat to a point within maxDistance that passes the filter, or null.</summary>
    public static Seat Nearest(Vector3 point, float maxDistance, Func<Seat, bool> filter = null)
    {
        all.RemoveAll(s => s == null);
        Seat best = null;
        float bestDist = maxDistance;
        foreach (var seat in all)
        {
            if (filter != null && !filter(seat))
                continue;
            float d = Vector3.Distance(point, seat.Position);
            if (d <= bestDist)
            {
                bestDist = d;
                best = seat;
            }
        }
        return best;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }
}
