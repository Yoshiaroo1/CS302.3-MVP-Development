using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// In-app folder browser for picking splat files at runtime.
/// Drawn with IMGUI so it needs no Canvas, EventSystem or prefabs, and works in builds.
///   Tab - show / hide the browser panel
/// Remembers the last folder between sessions.
/// </summary>
public class SplatFolderBrowser : MonoBehaviour
{
    public SplatLoader loader;
    public FlyCamera flyCamera;

    [Tooltip("File extensions shown in the browser (lower case, with dot).")]
    public string[] extensions = { ".ply", ".spz", ".sog" };

    [Tooltip("Folder to open on first launch. Leave empty for the user's Documents folder.")]
    public string startFolder = "";

    public bool showPanel = true;

    const string PrefsKey = "SplatBrowser.LastFolder";

    string currentFolder;
    string pathField;
    string listError;
    string pendingFolder;
    readonly List<string> subFolders = new();
    readonly List<FileInfo> splatFiles = new();
    Vector2 scroll;
    Rect panelRect;
    float uiScale = 1f;

    GUIStyle headerStyle, folderStyle, fileStyle, selectedStyle, labelStyle, boxStyle, hintStyle;
    bool stylesReady;

    void Start()
    {
        if (loader == null) loader = FindFirstObjectByType<SplatLoader>();
        if (flyCamera == null) flyCamera = FindFirstObjectByType<FlyCamera>();

        string saved = PlayerPrefs.GetString(PrefsKey, "");
        string initial =
            Directory.Exists(saved) ? saved :
            Directory.Exists(startFolder) ? startFolder :
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        Navigate(initial);
    }

    void Update()
    {
        if (!showPanel) FlyCamera.InputBlocked = false;

        // Folder changes requested by the UI are applied here, between frames.
        if (pendingFolder != null)
        {
            string folder = pendingFolder;
            pendingFolder = null;
            Navigate(folder);
        }
    }

    /// <summary>
    /// IMGUI uses Tab to move keyboard focus between text fields, so Tab is caught here,
    /// before any controls are drawn, and swallowed so the path box never grabs focus.
    /// </summary>
    void HandleTabToggle()
    {
        Event e = Event.current;
        if (e.type != EventType.KeyDown) return;

        // IMGUI sends Tab twice: once as a key code and once as a '\t' character.
        if (e.keyCode == KeyCode.Tab)
        {
            showPanel = !showPanel;
            GUIUtility.keyboardControl = 0;
            e.Use();
        }
        else if (e.character == '\t')
        {
            e.Use();
        }
    }

    // ---------- Navigation ----------

    void Navigate(string folder)
    {
        try
        {
            folder = Path.GetFullPath(folder);
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists) { listError = "Folder not found."; return; }

            var folders = new List<string>();
            var files = new List<FileInfo>();

            foreach (var d in dir.GetDirectories())
            {
                if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                folders.Add(d.FullName);
            }
            foreach (var f in dir.GetFiles())
            {
                if (Array.IndexOf(extensions, f.Extension.ToLowerInvariant()) >= 0)
                    files.Add(f);
            }

            folders.Sort(StringComparer.OrdinalIgnoreCase);
            files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            subFolders.Clear(); subFolders.AddRange(folders);
            splatFiles.Clear(); splatFiles.AddRange(files);
            currentFolder = folder;
            pathField = folder;
            listError = null;
            scroll = Vector2.zero;
            PlayerPrefs.SetString(PrefsKey, folder);
        }
        catch (UnauthorizedAccessException) { listError = "Access denied."; }
        catch (Exception e) { listError = e.Message; }
    }

    void GoUp()
    {
        var parent = Directory.GetParent(currentFolder);
        if (parent != null) Navigate(parent.FullName);
    }

    // ---------- Drawing ----------

    void InitStyles()
    {
        boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(10, 10, 10, 10) };
        headerStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
        labelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
        hintStyle.normal.textColor = new Color(1f, 1f, 1f, 0.6f);
        folderStyle = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft };
        fileStyle = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft };
        fileStyle.normal.textColor = new Color(0.6f, 0.9f, 1f);
        fileStyle.hover.textColor = Color.white;
        selectedStyle = new GUIStyle(fileStyle);
        selectedStyle.normal.textColor = new Color(0.4f, 1f, 0.5f);
        stylesReady = true;
    }

    void OnGUI()
    {
        if (!stylesReady) InitStyles();
        HandleTabToggle();

        // Scale UI with resolution so it stays readable on high-DPI screens.
        uiScale = Mathf.Max(1f, Screen.height / 1080f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(uiScale, uiScale, 1f));
        float w = Screen.width / uiScale, h = Screen.height / uiScale;

        DrawStatus(w, h);

        if (!showPanel) return;

        panelRect = new Rect(10, 10, Mathf.Min(420, w - 20), h - 60);
        GUILayout.BeginArea(panelRect, boxStyle);
        DrawPanel();
        GUILayout.EndArea();

        // Stop the fly camera while the cursor is over the panel or a text field has focus.
        if (Event.current.type == EventType.Repaint)
        {
            bool overPanel = panelRect.Contains(Event.current.mousePosition);
            FlyCamera.InputBlocked = overPanel || GUIUtility.keyboardControl != 0;
        }
    }

    void DrawPanel()
    {
        GUILayout.Label("Open splat", headerStyle);

        // Path bar
        GUILayout.BeginHorizontal();
        GUI.SetNextControlName("PathField");
        pathField = GUILayout.TextField(pathField ?? "", GUILayout.ExpandWidth(true));
        if (GUILayout.Button("Go", GUILayout.Width(40)) ||
            (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return &&
             GUI.GetNameOfFocusedControl() == "PathField"))
        {
            GUIUtility.keyboardControl = 0;
            pendingFolder = pathField;
        }
        GUILayout.EndHorizontal();

        // Quick places
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Up")) { var parent = Directory.GetParent(currentFolder); if (parent != null) pendingFolder = parent.FullName; }
        if (GUILayout.Button("Home")) pendingFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (GUILayout.Button("Desktop")) pendingFolder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (GUILayout.Button("Documents")) pendingFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        GUILayout.EndHorizontal();

        // Drives (Windows) / root
        GUILayout.BeginHorizontal();
        foreach (var drive in Directory.GetLogicalDrives())
        {
            if (GUILayout.Button(drive.TrimEnd('\\', '/') == "" ? "/" : drive.TrimEnd('\\'), GUILayout.ExpandWidth(false)))
                pendingFolder = drive;
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(6);

        if (listError != null)
            GUILayout.Label("Error: " + listError, labelStyle);

        // Listing. Clicks are recorded and acted on after the loops, because navigating
        // rebuilds these lists and a list can't change while it's being looped over.
        string folderToOpen = null, fileToLoad = null;

        scroll = GUILayout.BeginScrollView(scroll);
        foreach (var folder in subFolders)
        {
            if (GUILayout.Button("[ " + Path.GetFileName(folder) + " ]", folderStyle))
                folderToOpen = folder;
        }
        foreach (var file in splatFiles)
        {
            bool isCurrent = loader != null && loader.CurrentFile == file.FullName;
            string label = $"{file.Name}   ({FormatSize(file.Length)})";
            GUI.enabled = loader != null && !loader.IsLoading;
            if (GUILayout.Button(label, isCurrent ? selectedStyle : fileStyle))
                fileToLoad = file.FullName;
            GUI.enabled = true;
        }
        if (subFolders.Count == 0 && splatFiles.Count == 0 && listError == null)
            GUILayout.Label("No folders or splat files here.", hintStyle);
        GUILayout.EndScrollView();

        if (folderToOpen != null) pendingFolder = folderToOpen;
        if (fileToLoad != null) LoadFile(fileToLoad);

        GUILayout.Label("Tab: hide panel  |  RMB: look  |  WASD/QE: move  |  Shift: fast  |  Scroll: speed  |  R: reset view", hintStyle);
    }

    void DrawStatus(float w, float h)
    {
        if (loader == null)
        {
            GUI.Label(new Rect(10, h - 40, w - 20, 30), "No SplatLoader in scene.", labelStyle);
            return;
        }

        string status =
            loader.IsLoading ? "Loading... (large files can take a few seconds)" :
            loader.LastError != null ? "Load failed: " + loader.LastError :
            loader.CurrentFile != null ? "Viewing: " + Path.GetFileName(loader.CurrentFile) :
            "No splat loaded. Pick a file from the panel.";

        GUI.Label(new Rect(10, h - 40, w - 20, 30), status, labelStyle);
    }

    void LoadFile(string path)
    {
        if (loader == null) return;
        loader.Load(path);
        if (flyCamera != null) flyCamera.ResetView();
    }

    static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
        return $"{bytes / 1024.0:0} KB";
    }
}
