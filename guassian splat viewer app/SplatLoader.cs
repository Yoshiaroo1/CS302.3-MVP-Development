using System;
using System.Collections;
using UnityEngine;
using Gsplat;

/// <summary>
/// Loads Gaussian splat files at runtime into a GsplatRenderer.
/// Put this on the same GameObject as the Gsplat Renderer component.
/// </summary>
[RequireComponent(typeof(GsplatRenderer))]
public class SplatLoader : MonoBehaviour
{
    [Tooltip("Spark is packed and uses less memory. Uncompressed keeps exact values but needs more VRAM.")]
    public CompressionMode compression = CompressionMode.Spark;

    [Tooltip("Splats dragged into the editor get a 180 degree Z rotation automatically. Runtime loads need it applied here.")]
    public bool applyStandardRotation = true;

    public bool IsLoading { get; private set; }
    public string LastError { get; private set; }
    public string CurrentFile { get; private set; }

    /// <summary>Raised after a successful load with the file path.</summary>
    public event Action<string> Loaded;

    GsplatRenderer splatRenderer;
    GsplatAsset currentAsset;

    void Awake()
    {
        splatRenderer = GetComponent<GsplatRenderer>();
        if (applyStandardRotation)
            transform.localRotation = Quaternion.Euler(0f, 0f, 180f);
    }

    /// <summary>Starts loading a file. Waits a frame first so the UI can show "Loading...".</summary>
    public void Load(string absolutePath)
    {
        if (IsLoading) return;
        StartCoroutine(LoadRoutine(absolutePath));
    }

    IEnumerator LoadRoutine(string path)
    {
        IsLoading = true;
        LastError = null;

        // Let the UI draw a loading state before the (synchronous) parse blocks the main thread.
        yield return null;
        yield return null;

        GsplatAsset newAsset = null;
        try
        {
            newAsset = GsplatRuntimeLoader.LoadFile(path, compression);
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Debug.LogError($"Failed to load splat '{path}': {e}");
        }

        if (newAsset != null)
        {
            // Swap in stages so the renderer never draws with buffers from the wrong asset:
            // 1. detach the old asset and let the renderer release its GPU buffers in its Update,
            // 2. attach the new one and let the renderer bind and sort it,
            // 3. only then destroy the old asset.
            GsplatAsset oldAsset = currentAsset;
            if (oldAsset != null)
            {
                splatRenderer.GsplatAsset = null;
                yield return null;
            }

            currentAsset = newAsset;
            splatRenderer.GsplatAsset = currentAsset;
            CurrentFile = path;
            yield return null;

            if (oldAsset != null)
                Destroy(oldAsset);

            Loaded?.Invoke(path);
        }

        IsLoading = false;
    }

    public void Unload()
    {
        if (currentAsset == null) return;
        if (splatRenderer != null)
            splatRenderer.GsplatAsset = null;
        Destroy(currentAsset);
        currentAsset = null;
        CurrentFile = null;
    }

    void OnDestroy() => Unload();
}
