using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Gsplat;
using Gsplat.Formats;
using UnityEngine;

namespace ForensicNeRF
{
    /// <summary>
    /// One loaded Gaussian splat scan. Loads files at runtime, keeps a copy of the splat
    /// centres so the user can click on the scan, and owns the crop box.
    /// </summary>
    [RequireComponent(typeof(GsplatRenderer))]
    public class ScanSlot : MonoBehaviour
    {
        public CompressionMode compression = CompressionMode.Spark;

        [Tooltip("Splats more transparent than this are ignored when clicking on the scan, which skips haze and floaters.")]
        [Range(0f, 1f)] public float pickMinOpacity = 0.3f;

        [Tooltip("How close to a splat centre (in screen pixels) a click has to land.")]
        public float pickRadiusPixels = 6f;

        public bool IsLoading { get; private set; }
        public float LoadProgress => progress;
        public string LoadStage => stage;
        public string LastError { get; private set; }
        public string FilePath { get; private set; }
        public long FileBytes { get; private set; }
        public int SplatCount { get; private set; }
        public int SHBands { get; private set; }
        public float LoadSeconds { get; private set; }
        public bool HasScan => asset != null;

        /// <summary>Bounds of every splat, in scan coordinates. Often inflated by stray floaters.</summary>
        public Bounds LocalBounds { get; private set; }

        /// <summary>Bounds of the central 96% of the scan, in scan coordinates. Use this for framing.</summary>
        public Bounds RobustLocalBounds { get; private set; }

        public bool CropEnabled { get; private set; }
        public Bounds CropLocal { get; private set; }

        public event Action<ScanSlot> Loaded;
        public event Action<ScanSlot, string> LoadFailed;

        public bool Visible
        {
            get => splatRenderer != null && splatRenderer.enabled;
            set { if (splatRenderer != null && splatRenderer.enabled != value) splatRenderer.enabled = value; }
        }

        /// <summary>
        /// Most 3DGS scans need a 180 degree turn to stand upright in Unity.
        /// Flipped removes that turn for scans that are already upright.
        /// </summary>
        public bool Flipped
        {
            get => flipped;
            set { flipped = value; ApplyOrientation(); }
        }

        GsplatRenderer splatRenderer;
        GsplatAsset asset;
        Vector3[] pickPoints;
        GsplatCutout cropCutout;
        bool flipped;
        volatile float progress;
        volatile string stage = "";

        void Awake()
        {
            splatRenderer = GetComponent<GsplatRenderer>();
            ApplyOrientation();
        }

        void ApplyOrientation()
        {
            transform.localRotation = flipped ? Quaternion.identity : Quaternion.Euler(0f, 0f, 180f);
        }

        public Bounds RobustWorldBounds => TransformBounds(RobustLocalBounds);

        public Bounds TransformBounds(Bounds local)
        {
            Vector3 c = local.center, e = local.extents;
            var world = new Bounds(transform.TransformPoint(c), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    (i & 1) == 0 ? -e.x : e.x,
                    (i & 2) == 0 ? -e.y : e.y,
                    (i & 4) == 0 ? -e.z : e.z);
                world.Encapsulate(transform.TransformPoint(c + corner));
            }
            return world;
        }

        // ---------- Loading ----------

        public void Load(string path)
        {
            Debug.Log($"[ForensicNeRF] {name}: loading {path}");
            if (IsLoading) { Debug.Log($"[ForensicNeRF] {name}: ignored, already loading."); return; }
            StartCoroutine(LoadRoutine(path));
        }

        class DecodeResult
        {
            public Vector3[] pickPoints;
            public Bounds bounds;
            public Bounds robustBounds;
            public int count;
            public int shBands;
        }

        IEnumerator LoadRoutine(string path)
        {
            IsLoading = true;
            LastError = null;
            progress = 0f;
            stage = "Reading file";
            float startTime = Time.realtimeSinceStartup;

            string ext = Path.GetExtension(path).ToLowerInvariant();
            float minAlpha = pickMinOpacity;

            // The asset must be created on the main thread; filling it can happen in the background.
            GsplatAsset newAsset = compression == CompressionMode.Spark
                ? (GsplatAsset)ScriptableObject.CreateInstance<GsplatAssetSpark>()
                : ScriptableObject.CreateInstance<GsplatAssetUncompressed>();

            Task<DecodeResult> task;
            if (ext == ".sog")
            {
                // SOG decodes WebP images, which needs the main thread.
                yield return null;
                try { task = Task.FromResult(Decode(path, ext, newAsset, minAlpha)); }
                catch (Exception e) { task = Task.FromException<DecodeResult>(e); }
            }
            else
            {
                task = Task.Run(() => Decode(path, ext, newAsset, minAlpha));
            }

            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted || task.IsCanceled)
            {
                Exception e = task.Exception != null ? task.Exception.GetBaseException() : new Exception("Load cancelled.");
                LastError = e.Message;
                Debug.LogError($"ForensicNeRF: failed to load '{path}': {e}");
                Destroy(newAsset);
                IsLoading = false;
                LoadFailed?.Invoke(this, LastError);
                yield break;
            }

            DecodeResult result = task.Result;
            newAsset.name = Path.GetFileNameWithoutExtension(path);
            stage = "Uploading to GPU";

            // Swap in stages so the renderer never draws with buffers from the wrong asset.
            GsplatAsset oldAsset = asset;
            if (oldAsset != null)
            {
                splatRenderer.GsplatAsset = null;
                yield return null;
            }

            asset = newAsset;
            pickPoints = result.pickPoints;
            LocalBounds = result.bounds;
            RobustLocalBounds = result.robustBounds;
            SplatCount = result.count;
            SHBands = result.shBands;
            FilePath = path;
            FileBytes = new FileInfo(path).Length;
            splatRenderer.GsplatAsset = asset;
            yield return null;

            if (oldAsset != null) Destroy(oldAsset);

            SetCrop(false, RobustLocalBounds);
            LoadSeconds = Time.realtimeSinceStartup - startTime;
            progress = 1f;
            IsLoading = false;
            Debug.Log($"[ForensicNeRF] {name}: loaded {path} ({SplatCount:N0} splats).");
            Loaded?.Invoke(this);
        }

        DecodeResult Decode(string path, string ext, GsplatAsset target, float minAlpha)
        {
            ProgressCallback decodeProgress = (info, p) => { stage = "Decoding"; progress = p * 0.6f; };
            ProgressCallback packProgress = (info, p) => { stage = "Packing splats"; progress = 0.6f + p * 0.3f; };

            GsplatDecodedData data;
            using (var stream = File.OpenRead(path))
            {
                switch (ext)
                {
                    case ".ply": data = PlayCanvasPlyReader.Read(stream, SourceCoordinates.RUB, decodeProgress); break;
                    case ".spz": data = PlayCanvasSpzReader.Read(stream, SourceCoordinates.RUB, decodeProgress); break;
                    case ".sog": data = PlayCanvasSogReader.ReadBundle(stream, SourceCoordinates.RUB, decodeProgress); break;
                    default: throw new NotSupportedException($"Unsupported file type '{ext}'. Use .ply, .spz or .sog.");
                }
            }

            target.LoadFromDecoded(data, packProgress);

            stage = "Preparing picking";
            progress = 0.92f;

            // Keep only reasonably opaque splat centres for click picking.
            int keep = 0;
            for (int i = 0; i < data.Count; i++)
                if (data.Colors[i].w >= minAlpha) keep++;
            if (keep == 0) keep = data.Count; // fall back to everything for very faint scans

            var points = new Vector3[keep];
            bool filter = keep != data.Count;
            for (int i = 0, k = 0; i < data.Count; i++)
                if (!filter || data.Colors[i].w >= minAlpha)
                    points[k++] = data.Positions[i];

            return new DecodeResult
            {
                pickPoints = points,
                bounds = data.Bounds,
                robustBounds = ComputeRobustBounds(points, data.Bounds),
                count = data.Count,
                shBands = data.SHBands
            };
        }

        /// <summary>Bounds of the 2nd to 98th percentile on each axis, from an even sample.</summary>
        static Bounds ComputeRobustBounds(Vector3[] points, Bounds fallback)
        {
            if (points.Length < 16) return fallback;
            int samples = Mathf.Min(points.Length, 65536);
            float step = points.Length / (float)samples;
            var xs = new float[samples];
            var ys = new float[samples];
            var zs = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                Vector3 p = points[(int)(i * step)];
                xs[i] = p.x; ys[i] = p.y; zs[i] = p.z;
            }
            Array.Sort(xs); Array.Sort(ys); Array.Sort(zs);
            int lo = (int)(samples * 0.02f), hi = (int)(samples * 0.98f) - 1;
            var b = new Bounds();
            b.SetMinMax(new Vector3(xs[lo], ys[lo], zs[lo]), new Vector3(xs[hi], ys[hi], zs[hi]));
            if (b.size.sqrMagnitude < 1e-8f) return fallback;
            return b;
        }

        public void Unload()
        {
            if (asset == null) return;
            splatRenderer.GsplatAsset = null;
            Destroy(asset);
            asset = null;
            pickPoints = null;
            FilePath = null;
        }

        void OnDestroy() => Unload();

        // ---------- Picking ----------

        /// <summary>
        /// Finds the nearest opaque splat under a screen position.
        /// Returns the hit in world space.
        /// </summary>
        public bool Raycast(Camera cam, Vector2 screenPos, out Vector3 worldHit)
        {
            worldHit = default;
            if (pickPoints == null || pickPoints.Length == 0 || cam == null || !Visible) return false;

            Ray ray = cam.ScreenPointToRay(screenPos);
            Vector3 o = transform.InverseTransformPoint(ray.origin);
            Vector3 d = transform.InverseTransformDirection(ray.direction).normalized;

            float pixelAngle = pickRadiusPixels * cam.fieldOfView * Mathf.Deg2Rad / Mathf.Max(1, cam.pixelHeight);
            float tan = Mathf.Tan(pixelAngle);
            float tan2 = tan * tan;
            float minT = cam.nearClipPlane;

            bool crop = CropEnabled;
            Vector3 cMin = CropLocal.min, cMax = CropLocal.max;
            Vector3[] pts = pickPoints;

            // Pass 1: nearest splat inside a thin cone around the ray.
            float bestT = float.MaxValue;
            for (int i = 0; i < pts.Length; i++)
            {
                float vx = pts[i].x - o.x, vy = pts[i].y - o.y, vz = pts[i].z - o.z;
                float t = vx * d.x + vy * d.y + vz * d.z;
                if (t <= minT || t >= bestT) continue;
                float perp2 = vx * vx + vy * vy + vz * vz - t * t;
                if (perp2 > t * t * tan2) continue;
                if (crop && !Inside(pts[i], cMin, cMax)) continue;
                bestT = t;
            }
            if (bestT == float.MaxValue) return false;

            // Pass 2: average the splats just behind the nearest one, for a steadier surface point.
            float maxT = bestT * 1.03f + 0.01f;
            Vector3 sum = Vector3.zero;
            int count = 0;
            for (int i = 0; i < pts.Length; i++)
            {
                float vx = pts[i].x - o.x, vy = pts[i].y - o.y, vz = pts[i].z - o.z;
                float t = vx * d.x + vy * d.y + vz * d.z;
                if (t < bestT || t > maxT) continue;
                float perp2 = vx * vx + vy * vy + vz * vz - t * t;
                if (perp2 > t * t * tan2) continue;
                if (crop && !Inside(pts[i], cMin, cMax)) continue;
                sum += pts[i];
                count++;
            }

            Vector3 local = count > 0 ? sum / count : o + d * bestT;
            worldHit = transform.TransformPoint(local);
            return true;
        }

        static bool Inside(Vector3 p, Vector3 min, Vector3 max) =>
            p.x >= min.x && p.y >= min.y && p.z >= min.z && p.x <= max.x && p.y <= max.y && p.z <= max.z;

        // ---------- Crop ----------

        /// <summary>Hides every splat outside a box given in scan coordinates.</summary>
        public void SetCrop(bool enabled, Bounds localBox)
        {
            if (cropCutout == null)
            {
                var go = new GameObject("Crop Box");
                go.transform.SetParent(transform, false);
                cropCutout = go.AddComponent<GsplatCutout>();
                cropCutout.m_Type = GsplatCutout.Type.Box;
                cropCutout.m_Target = GsplatCutout.Target.Parent;
                cropCutout.m_Invert = false;
            }

            CropEnabled = enabled;
            CropLocal = localBox;

            // A box cutout spans -1..1 in its own space, so its scale is the half-size.
            var t = cropCutout.transform;
            t.localPosition = localBox.center;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.Max(localBox.extents, Vector3.one * 1e-4f);
            cropCutout.enabled = enabled;
        }
    }
}
