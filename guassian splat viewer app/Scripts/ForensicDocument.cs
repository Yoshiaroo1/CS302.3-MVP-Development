using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ForensicNeRF
{
    /// <summary>A numbered evidence marker placed on the scan.</summary>
    [Serializable]
    public class EvidenceItem
    {
        public int number;
        public string title = "";
        public string notes = "";
        [Tooltip("Position in the scan's own (local) coordinates, so it survives orientation changes.")]
        public Vector3 localPosition;
    }

    /// <summary>A point-to-point distance measured on the scan.</summary>
    [Serializable]
    public class Measurement
    {
        public int number;
        public Vector3 localA;
        public Vector3 localB;

        /// <summary>Length in scan units, before scale calibration.</summary>
        public float RawLength => Vector3.Distance(localA, localB);
    }

    /// <summary>
    /// Everything the user adds to a scan. Saved automatically next to the scan as
    /// "&lt;scan file&gt;.forensic.json" and reloaded when the scan is opened again.
    /// </summary>
    [Serializable]
    public class ForensicDocument
    {
        public int version = 1;
        public string scanFile;
        [Tooltip("Real-world metres per scan unit. Set by scale calibration.")]
        public float metresPerUnit = 1f;
        public bool flipped;
        public bool hasCropBox;
        public bool cropEnabled;
        public Vector3 cropMin;
        public Vector3 cropMax;
        public List<EvidenceItem> evidence = new List<EvidenceItem>();
        public List<Measurement> measurements = new List<Measurement>();

        [NonSerialized] public string sidecarPath;

        public int NextEvidenceNumber()
        {
            int n = 0;
            foreach (var e in evidence) n = Mathf.Max(n, e.number);
            return n + 1;
        }

        public int NextMeasurementNumber()
        {
            int n = 0;
            foreach (var m in measurements) n = Mathf.Max(n, m.number);
            return n + 1;
        }

        public static string SidecarPathFor(string scanPath) => scanPath + ".forensic.json";

        public static ForensicDocument LoadOrCreate(string scanPath)
        {
            string sidecar = SidecarPathFor(scanPath);
            ForensicDocument doc = null;
            try
            {
                if (File.Exists(sidecar))
                    doc = JsonUtility.FromJson<ForensicDocument>(File.ReadAllText(sidecar));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"ForensicNeRF: couldn't read annotations from {sidecar}: {e.Message}");
            }

            if (doc == null) doc = new ForensicDocument();
            if (doc.evidence == null) doc.evidence = new List<EvidenceItem>();
            if (doc.measurements == null) doc.measurements = new List<Measurement>();
            if (doc.metresPerUnit <= 0f) doc.metresPerUnit = 1f;
            doc.scanFile = Path.GetFileName(scanPath);
            doc.sidecarPath = sidecar;
            return doc;
        }

        public bool Save(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(sidecarPath)) { error = "No scan loaded."; return false; }
            try
            {
                File.WriteAllText(sidecarPath, JsonUtility.ToJson(this, true));
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }
    }

    // ---------- Export report format ----------

    [Serializable]
    public class ExportedEvidence
    {
        public int number;
        public string title;
        public string notes;
        public Vector3 positionMetres;
    }

    [Serializable]
    public class ExportedMeasurement
    {
        public int number;
        public Vector3 startMetres;
        public Vector3 endMetres;
        public float lengthMetres;
    }

    [Serializable]
    public class ExportReport
    {
        public string application = "ForensicNeRF";
        public string exportedAt;
        public string exportedBy;
        public string scanFile;
        public string scanPath;
        public int splatCount;
        public float metresPerUnit;
        public string screenshot = "screenshot.png";
        public List<ExportedEvidence> evidence = new List<ExportedEvidence>();
        public List<ExportedMeasurement> measurements = new List<ExportedMeasurement>();
    }
}
