using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace ForensicNeRF
{
    /// <summary>
    /// Drives the ForensicNeRF viewer UI (ForensicUI.uxml). Put this on the GameObject with the UIDocument.
    /// Scan objects and the camera controller are created automatically if not assigned.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    [DefaultExecutionOrder(100)]
    public class ForensicUI : MonoBehaviour
    {
        [Header("Scene (created automatically if left empty)")]
        public ViewerCamera viewer;
        public ScanSlot scanA;
        public ScanSlot scanB;

        [Header("User")]
        public string userName = "Investigator";
        public string userInitials = "RH";

        [Header("Files")]
        public string[] extensions = { ".ply", ".spz", ".sog" };

        enum Tool { Select, AddEvidence, Measure, Crop, Focus }
        enum Mode { Orbit, Measure, Annotate, Compare }
        enum CompareView { A, B, Both }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Color MeasureRed = new Color(0.85f, 0.23f, 0.20f);
        static readonly Color MeasureRedSelected = new Color(1f, 0.47f, 0.42f);
        static readonly Color CropTeal = new Color(0.24f, 0.75f, 0.89f, 0.9f);

        // UI references
        VisualElement root, app, viewport, markerLayer, emptyState, loadingPanel, progressFill, toast;
        VisualElement cropPanel, compareBar, detailsPanel, evidencePage, infoPage, evidenceList, measureList, infoRows;
        VisualElement sceneButton, exportButton, tabEvidence, tabInfo;
        Label sceneLabel, loadingLabel, toolHint, toastLabel, toastAction, evidenceEmpty, measureEmpty;
        Label compareChoose, detailsBadge, detailsCoords, scaleLabel, avatarButton;
        CanvasElement overlay;
        AxisGizmo gizmo;
        FilePickerModal picker;
        TextField titleField, notesField;
        FloatField calibLength;
        Slider nearClipSlider;
        Toggle flipToggle, cropToggle;
        MinMaxSlider cropX, cropY, cropZ;
        VisualElement menuBackdrop;

        readonly Dictionary<Tool, VisualElement> toolButtons = new Dictionary<Tool, VisualElement>();
        readonly Dictionary<Mode, VisualElement> modeTabs = new Dictionary<Mode, VisualElement>();
        readonly Dictionary<CompareView, VisualElement> compareButtons = new Dictionary<CompareView, VisualElement>();
        readonly Dictionary<string, Label> infoValues = new Dictionary<string, Label>();
        readonly Dictionary<object, VisualElement> rowFor = new Dictionary<object, VisualElement>();
        readonly Dictionary<EvidenceItem, Label> titleLabelFor = new Dictionary<EvidenceItem, Label>();
        readonly List<Label> markerPool = new List<Label>();
        readonly List<Label> measurePool = new List<Label>();

        // State
        ForensicDocument doc;
        Tool tool = Tool.Select;
        Mode mode = Mode.Orbit;
        CompareView compareView = CompareView.Both;
        EvidenceItem selectedEvidence;
        Measurement selectedMeasurement;
        bool hasPendingPoint;
        Vector3 pendingLocal;
        Vector2 pointerLocal;
        bool pointerInViewport;
        int dragPointer = -1;
        int dragButton;
        Vector2 dragStart;
        bool dragging, dragPan;
        bool docDirty;
        float lastEditTime;
        bool listsDirty;
        bool exporting;
        bool evidenceTabActive = true;
        Action toastCallback;
        IVisualElementScheduledItem toastTimer;

        // ======================================================================
        // Setup
        // ======================================================================

        void Start()
        {
            root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null) { Debug.LogError("ForensicUI: UIDocument has no root. Assign ForensicUI.uxml as its Source Asset."); enabled = false; return; }
            root.style.flexGrow = 1;
            root.style.position = Position.Absolute;
            root.style.left = 0;
            root.style.top = 0;
            root.style.right = 0;
            root.style.bottom = 0;

            EnsureSceneObjects();
            if (!QueryElements()) { enabled = false; return; }
            BuildDynamicParts();
            BindControls();

            picker = new FilePickerModal(root, extensions);
            scanA.Loaded += OnScanALoaded;
            scanB.Loaded += OnScanBLoaded;
            scanA.LoadFailed += OnScanFailed;
            scanB.LoadFailed += OnScanFailed;

            avatarButton.text = userInitials;
            nearClipSlider.SetValueWithoutNotify(viewer.Cam.nearClipPlane);
            viewer.Cam.clearFlags = CameraClearFlags.SolidColor;
            viewer.Cam.backgroundColor = new Color(0.039f, 0.071f, 0.098f);

            SetSideTab(true);
            SetTool(Tool.Select);
            RefreshInfo();
            RebuildLists();
        }

        void EnsureSceneObjects()
        {
            if (viewer == null) viewer = FindFirstObjectByType<ViewerCamera>();
            if (viewer == null)
            {
                var cam = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
                if (cam == null) cam = new GameObject("Main Camera").AddComponent<Camera>();
                viewer = cam.gameObject.AddComponent<ViewerCamera>();
            }
            if (scanA == null) scanA = new GameObject("Scan A").AddComponent<ScanSlot>();
            if (scanB == null) scanB = new GameObject("Scan B").AddComponent<ScanSlot>();
        }

        T Find<T>(string name) where T : VisualElement
        {
            var e = root.Q<T>(name);
            if (e == null) Debug.LogError($"ForensicUI: element '{name}' ({typeof(T).Name}) is missing from the UXML.");
            return e;
        }

        bool QueryElements()
        {
            app = Find<VisualElement>("app");
            viewport = Find<VisualElement>("viewport");
            markerLayer = Find<VisualElement>("marker-layer");
            emptyState = Find<VisualElement>("empty-state");
            loadingPanel = Find<VisualElement>("loading");
            progressFill = Find<VisualElement>("progress-fill");
            loadingLabel = Find<Label>("loading-label");
            toast = Find<VisualElement>("toast");
            toastLabel = Find<Label>("toast-label");
            toastAction = Find<Label>("toast-action");
            toolHint = Find<Label>("tool-hint");
            cropPanel = Find<VisualElement>("crop-panel");
            compareBar = Find<VisualElement>("compare-bar");
            compareChoose = Find<Label>("compare-choose");
            detailsPanel = Find<VisualElement>("evidence-details");
            detailsBadge = Find<Label>("details-badge");
            detailsCoords = Find<Label>("details-coords");
            evidencePage = Find<VisualElement>("evidence-page");
            infoPage = Find<VisualElement>("info-page");
            evidenceList = Find<VisualElement>("evidence-list");
            measureList = Find<VisualElement>("measure-list");
            evidenceEmpty = Find<Label>("evidence-empty");
            measureEmpty = Find<Label>("measure-empty");
            infoRows = Find<VisualElement>("info-rows");
            sceneButton = Find<VisualElement>("scene-button");
            sceneLabel = Find<Label>("scene-label");
            exportButton = Find<VisualElement>("export-button");
            avatarButton = Find<Label>("avatar-button");
            tabEvidence = Find<VisualElement>("tab-evidence");
            tabInfo = Find<VisualElement>("tab-info");
            titleField = Find<TextField>("ev-title");
            notesField = Find<TextField>("ev-notes");
            calibLength = Find<FloatField>("calib-length");
            nearClipSlider = Find<Slider>("near-clip");
            flipToggle = Find<Toggle>("flip-toggle");
            cropToggle = Find<Toggle>("crop-enabled");
            cropX = Find<MinMaxSlider>("crop-x");
            cropY = Find<MinMaxSlider>("crop-y");
            cropZ = Find<MinMaxSlider>("crop-z");
            scaleLabel = Find<Label>("scale-label");

            return app != null && viewport != null && markerLayer != null && titleField != null && cropX != null
                   && nearClipSlider != null && infoRows != null && evidenceList != null;
        }

        void BuildDynamicParts()
        {
            // Icons
            Find<VisualElement>("logo-slot")?.Add(new VectorIcon(IconKind.Logo, 34f));
            Find<VisualElement>("scene-caret")?.Add(new VectorIcon(IconKind.Caret, 14f));
            Find<VisualElement>("tool-select")?.Add(new VectorIcon(IconKind.Select, 24f));
            Find<VisualElement>("tool-add")?.Add(new VectorIcon(IconKind.Add, 24f));
            Find<VisualElement>("tool-measure")?.Add(new VectorIcon(IconKind.Measure, 24f));
            Find<VisualElement>("tool-crop")?.Add(new VectorIcon(IconKind.Crop, 24f));
            Find<VisualElement>("tool-pin")?.Add(new VectorIcon(IconKind.Pin, 24f));
            Find<VisualElement>("zoom-in")?.Add(new VectorIcon(IconKind.Add, 20f));
            Find<VisualElement>("zoom-out")?.Add(new VectorIcon(IconKind.Minus, 20f));

            // Measurement lines and crop box are drawn on this canvas
            overlay = new CanvasElement();
            overlay.AddToClassList("fill");
            overlay.OnDraw = DrawOverlay;
            Find<VisualElement>("overlay-slot")?.Add(overlay);

            gizmo = new AxisGizmo();
            gizmo.AxisClicked = axis => viewer.SnapToAxis(axis);
            Find<VisualElement>("gizmo-slot")?.Add(gizmo);

            // Scan info rows
            foreach (var key in new[] { "File", "Folder", "File size", "Splats", "SH bands", "Extent", "Load time", "Annotations" })
            {
                var row = UIBuild.Div(infoRows, "info-row");
                UIBuild.Text(row, key, "info-key");
                infoValues[key] = UIBuild.Text(row, "-", "info-value");
            }
        }

        VisualElement Bind(string name, Action action)
        {
            var e = Find<VisualElement>(name);
            e?.AddManipulator(new Clickable(action));
            return e;
        }

        void BindControls()
        {
            // Top bar
            Bind("scene-button", OpenScanPicker);
            Bind("empty-open", OpenScanPicker);
            modeTabs[Mode.Orbit] = Bind("mode-orbit", () => SetMode(Mode.Orbit));
            modeTabs[Mode.Measure] = Bind("mode-measure", () => SetMode(Mode.Measure));
            modeTabs[Mode.Annotate] = Bind("mode-annotate", () => SetMode(Mode.Annotate));
            modeTabs[Mode.Compare] = Bind("mode-compare", () => SetMode(Mode.Compare));
            Bind("export-button", () => { if (!exporting) StartCoroutine(ExportRoutine()); });
            Bind("avatar-button", ToggleUserMenu);

            // Tool strip
            toolButtons[Tool.Select] = Bind("tool-select", () => SetTool(Tool.Select));
            toolButtons[Tool.AddEvidence] = Bind("tool-add", () => SetTool(Tool.AddEvidence));
            toolButtons[Tool.Measure] = Bind("tool-measure", () => SetTool(Tool.Measure));
            toolButtons[Tool.Crop] = Bind("tool-crop", () => SetTool(tool == Tool.Crop ? Tool.Select : Tool.Crop));
            toolButtons[Tool.Focus] = Bind("tool-pin", () => SetTool(Tool.Focus));

            // Viewport controls
            Bind("zoom-in", () => viewer.Dolly(-1f));
            Bind("zoom-out", () => viewer.Dolly(1f));
            toastAction.AddManipulator(new Clickable(() => toastCallback?.Invoke()));

            // Side panel
            tabEvidence = Bind("tab-evidence", () => SetSideTab(true));
            tabInfo = Bind("tab-info", () => SetSideTab(false));
            Bind("ev-focus", () => { if (selectedEvidence != null) viewer.FocusOn(ToWorld(selectedEvidence.localPosition)); });
            Bind("ev-delete", DeleteSelected);
            titleField.RegisterValueChangedCallback(evt =>
            {
                if (selectedEvidence == null) return;
                selectedEvidence.title = evt.newValue;
                if (titleLabelFor.TryGetValue(selectedEvidence, out var label)) label.text = evt.newValue;
                MarkDocDirty();
            });
            notesField.RegisterValueChangedCallback(evt =>
            {
                if (selectedEvidence == null) return;
                selectedEvidence.notes = evt.newValue;
                MarkDocDirty();
            });

            // Crop
            cropToggle.RegisterValueChangedCallback(_ => ApplyCropFromUI());
            cropX.RegisterValueChangedCallback(_ => ApplyCropFromUI());
            cropY.RegisterValueChangedCallback(_ => ApplyCropFromUI());
            cropZ.RegisterValueChangedCallback(_ => ApplyCropFromUI());
            Bind("crop-reset", ResetCropBox);

            // Compare
            Bind("compare-choose", OpenScanBPicker);
            compareButtons[CompareView.A] = Bind("compare-a", () => SetCompareView(CompareView.A));
            compareButtons[CompareView.B] = Bind("compare-b", () => SetCompareView(CompareView.B));
            compareButtons[CompareView.Both] = Bind("compare-both", () => SetCompareView(CompareView.Both));

            // Scan info
            nearClipSlider.RegisterValueChangedCallback(evt => viewer.Cam.nearClipPlane = Mathf.Max(0.001f, evt.newValue));
            flipToggle.RegisterValueChangedCallback(evt => SetFlipped(evt.newValue));
            Bind("frame-button", FrameScan);
            Bind("top-view-button", () => viewer.SnapToAxis(Vector3.up));
            Bind("calibrate-button", Calibrate);
            Bind("reset-scale-button", () =>
            {
                if (doc == null) return;
                doc.metresPerUnit = 1f;
                MarkDocDirty();
                listsDirty = true;
                RefreshInfo();
                Toast("Scale reset to 1 scan unit = 1 m.");
            });

            // Viewport mouse handling
            viewport.RegisterCallback<PointerDownEvent>(OnViewportDown);
            viewport.RegisterCallback<PointerMoveEvent>(OnViewportMove);
            viewport.RegisterCallback<PointerUpEvent>(OnViewportUp);
            viewport.RegisterCallback<PointerCaptureOutEvent>(_ => { dragPointer = -1; dragging = false; });
            viewport.RegisterCallback<WheelEvent>(OnViewportWheel);
            viewport.RegisterCallback<PointerEnterEvent>(_ => pointerInViewport = true);
            viewport.RegisterCallback<PointerLeaveEvent>(_ => pointerInViewport = false);
        }

        // ======================================================================
        // Per-frame
        // ======================================================================

        void Update()
        {
            if (root == null) return;
            ReadKeys(out bool esc, out bool del, out bool frame);

            if (picker.IsOpen) { if (esc) picker.Close(); return; }
            if (menuBackdrop != null) { if (esc) CloseUserMenu(); return; }
            if (IsTyping())
            {
                if (esc) (root.panel?.focusController?.focusedElement)?.Blur();
                return;
            }

            if (esc)
            {
                if (hasPendingPoint) hasPendingPoint = false;
                else if (tool != Tool.Select) SetTool(Tool.Select);
                else Deselect();
                UpdateToolHint();
            }
            if (del) DeleteSelected();
            if (frame) FrameScan();
        }

        void LateUpdate()
        {
            if (root == null || root.panel == null) return;

            UpdateCameraRect();
            UpdateLoadingUI();
            gizmo.Refresh(viewer != null ? viewer.Cam : null);
            overlay.MarkDirtyRepaint();
            UpdateMarkers();

            if (listsDirty)
            {
                listsDirty = false;
                RebuildLists();
            }
            if (docDirty && Time.unscaledTime - lastEditTime > 0.6f) SaveDoc();
        }

        void OnApplicationQuit()
        {
            if (docDirty) SaveDoc();
        }

        static void ReadKeys(out bool esc, out bool del, out bool frame)
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            esc = kb != null && kb.escapeKey.wasPressedThisFrame;
            del = kb != null && (kb.deleteKey.wasPressedThisFrame || kb.backspaceKey.wasPressedThisFrame);
            frame = kb != null && kb.fKey.wasPressedThisFrame;
#else
            esc = Input.GetKeyDown(KeyCode.Escape);
            del = Input.GetKeyDown(KeyCode.Delete) || Input.GetKeyDown(KeyCode.Backspace);
            frame = Input.GetKeyDown(KeyCode.F);
#endif
        }

        bool IsTyping()
        {
            var focused = root.panel?.focusController?.focusedElement as VisualElement;
            if (focused == null) return false;
            return focused is TextField || focused.GetFirstAncestorOfType<TextField>() != null
                || focused is FloatField || focused.GetFirstAncestorOfType<FloatField>() != null;
        }

        // ---------- Screen / UI coordinate conversion ----------

        Rect PanelRect => root.panel.visualTree.layout;

        Vector2 PanelToScreen(Vector2 panelPos)
        {
            Rect pr = PanelRect;
            float sx = Screen.width / Mathf.Max(1f, pr.width);
            float sy = Screen.height / Mathf.Max(1f, pr.height);
            return new Vector2(panelPos.x * sx, Screen.height - panelPos.y * sy);
        }

        /// <summary>Projects a world point into the viewport's local UI coordinates.</summary>
        bool WorldToViewport(Vector3 world, out Vector2 local)
        {
            local = default;
            if (viewer == null) return false;
            Vector3 s = viewer.Cam.WorldToScreenPoint(world);
            if (s.z <= viewer.Cam.nearClipPlane) return false;
            Rect pr = PanelRect;
            float sx = Screen.width / Mathf.Max(1f, pr.width);
            float sy = Screen.height / Mathf.Max(1f, pr.height);
            var panelPos = new Vector2(s.x / sx, (Screen.height - s.y) / sy);
            local = viewport.WorldToLocal(panelPos);
            return true;
        }

        void UpdateCameraRect()
        {
            if (viewer == null) return;
            Rect pr = PanelRect;
            Rect vb = viewport.worldBound;
            if (pr.width <= 0f || pr.height <= 0f || float.IsNaN(vb.width) || vb.width <= 0f) return;
            viewer.SetViewport(new Rect(
                vb.xMin / pr.width,
                1f - vb.yMax / pr.height,
                vb.width / pr.width,
                vb.height / pr.height));
        }

        Vector3 ToWorld(Vector3 local) => scanA.transform.TransformPoint(local);

        float Mpu => doc != null ? doc.metresPerUnit : 1f;

        // ======================================================================
        // Viewport mouse
        // ======================================================================

        void OnViewportDown(PointerDownEvent evt)
        {
            if (evt.target != viewport || viewer == null) return;

            if (evt.button == 0 && evt.clickCount == 2 && tool == Tool.Select)
            {
                if (Pick(evt.position, out Vector3 hit))
                {
                    viewer.FocusOn(hit);
                    Toast("Orbit centre moved to that point.");
                }
                return;
            }

            dragPointer = evt.pointerId;
            dragButton = evt.button;
            dragStart = evt.position;
            dragging = false;
            dragPan = evt.button != 0 || evt.shiftKey;
            viewport.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        void OnViewportMove(PointerMoveEvent evt)
        {
            pointerLocal = viewport.WorldToLocal(evt.position);
            pointerInViewport = true;
            if (evt.pointerId != dragPointer || !viewport.HasPointerCapture(evt.pointerId)) return;

            if (!dragging && ((Vector2)evt.position - dragStart).sqrMagnitude > 16f) dragging = true;
            if (!dragging) return;

            Vector2 delta = evt.deltaPosition;
            if (dragPan) viewer.Pan(delta, viewport.layout.height);
            else viewer.Orbit(delta);
        }

        void OnViewportUp(PointerUpEvent evt)
        {
            if (evt.pointerId != dragPointer) return;
            bool wasDrag = dragging;
            int button = dragButton;
            dragPointer = -1;
            dragging = false;
            if (viewport.HasPointerCapture(evt.pointerId)) viewport.ReleasePointer(evt.pointerId);
            if (!wasDrag && button == 0) OnViewportClick(evt.position);
        }

        void OnViewportWheel(WheelEvent evt)
        {
            if (viewer == null) return;
            if (evt.target != viewport && evt.target != overlay) return;
            if (Mathf.Abs(evt.delta.y) > 0.001f) viewer.Dolly(Mathf.Sign(evt.delta.y));
            evt.StopPropagation();
        }

        void OnViewportClick(Vector2 panelPos)
        {
            if (!scanA.HasScan || doc == null) return;
            switch (tool)
            {
                case Tool.Select:
                    Deselect();
                    break;

                case Tool.AddEvidence:
                    if (PickLocal(panelPos, out Vector3 local)) AddEvidence(local);
                    else Toast("Nothing under the cursor. Click directly on the scan.");
                    break;

                case Tool.Measure:
                    if (!PickLocal(panelPos, out Vector3 point))
                    {
                        Toast("Nothing under the cursor. Click directly on the scan.");
                        break;
                    }
                    if (!hasPendingPoint)
                    {
                        pendingLocal = point;
                        hasPendingPoint = true;
                    }
                    else
                    {
                        hasPendingPoint = false;
                        AddMeasurement(pendingLocal, point);
                    }
                    UpdateToolHint();
                    break;

                case Tool.Focus:
                    if (Pick(panelPos, out Vector3 hit))
                    {
                        viewer.FocusOn(hit);
                        Toast("Orbit centre set.");
                    }
                    else Toast("Nothing under the cursor. Click directly on the scan.");
                    break;
            }
        }

        bool Pick(Vector2 panelPos, out Vector3 world)
        {
            world = default;
            if (!scanA.HasScan || viewer == null) return false;
            return scanA.Raycast(viewer.Cam, PanelToScreen(panelPos), out world);
        }

        bool PickLocal(Vector2 panelPos, out Vector3 local)
        {
            local = default;
            if (!Pick(panelPos, out Vector3 world)) return false;
            local = scanA.transform.InverseTransformPoint(world);
            return true;
        }

        // ======================================================================
        // Tools and modes
        // ======================================================================

        void SetMode(Mode m)
        {
            if (m == Mode.Compare && mode == Mode.Compare) m = Mode.Orbit; // clicking Compare again closes it
            mode = m;
            switch (m)
            {
                case Mode.Measure: ApplyTool(Tool.Measure); break;
                case Mode.Annotate: ApplyTool(Tool.AddEvidence); break;
                default: ApplyTool(Tool.Select); break;
            }
            UpdateModeUI();
        }

        void SetTool(Tool t)
        {
            ApplyTool(t);
            if (t == Tool.Measure) mode = Mode.Measure;
            else if (t == Tool.AddEvidence) mode = Mode.Annotate;
            else if (mode != Mode.Compare) mode = Mode.Orbit;
            UpdateModeUI();
        }

        void ApplyTool(Tool t)
        {
            tool = t;
            if (t != Tool.Measure) hasPendingPoint = false;
            foreach (var kv in toolButtons)
            {
                kv.Value?.EnableInClassList("selected", kv.Key == t);
                UIBuild.RepaintIcons(kv.Value);
            }
            cropPanel.style.display = t == Tool.Crop ? DisplayStyle.Flex : DisplayStyle.None;
            UpdateToolHint();
        }

        void UpdateModeUI()
        {
            foreach (var kv in modeTabs) kv.Value?.EnableInClassList("active", kv.Key == mode);
            compareBar.style.display = mode == Mode.Compare ? DisplayStyle.Flex : DisplayStyle.None;
            UpdateCompareUI();
        }

        void UpdateToolHint()
        {
            string hint;
            switch (tool)
            {
                case Tool.AddEvidence: hint = "Click on the scan to place an evidence marker"; break;
                case Tool.Measure:
                    hint = hasPendingPoint ? "Click the second point  ·  Esc to cancel" : "Click the first point to measure from";
                    break;
                case Tool.Crop: hint = "Drag the sliders to set the crop box"; break;
                case Tool.Focus: hint = "Click on the scan to set the orbit centre"; break;
                default: hint = "Drag to orbit  ·  Right-drag to pan  ·  Scroll to zoom  ·  Double-click to centre"; break;
            }
            toolHint.text = hint;
        }

        // ======================================================================
        // Loading scans
        // ======================================================================

        void OpenScanPicker()
        {
            Debug.Log("[ForensicNeRF] Open scan clicked.");
            if (scanA.IsLoading)
            {
                Debug.Log("[ForensicNeRF] Ignored: a scan is still loading.");
                Toast("A scan is still loading. Wait for it to finish, then try again.");
                return;
            }
            picker.Show("Open scan", path =>
            {
                Debug.Log("[ForensicNeRF] Picked " + path);
                if (docDirty) SaveDoc();
                scanA.Load(path);
            });
        }

        void OpenScanBPicker()
        {
            if (scanB.IsLoading) { Toast("Scan B is still loading."); return; }
            picker.Show("Open comparison scan (B)", path => scanB.Load(path));
        }

        void OnScanALoaded(ScanSlot slot)
        {
            doc = ForensicDocument.LoadOrCreate(slot.FilePath);
            scanA.Flipped = doc.flipped;
            scanB.Flipped = doc.flipped;
            flipToggle.SetValueWithoutNotify(doc.flipped);
            ApplyCropFromDoc();

            sceneLabel.text = Path.GetFileNameWithoutExtension(slot.FilePath);
            sceneButton.tooltip = slot.FilePath;
            selectedEvidence = null;
            selectedMeasurement = null;
            hasPendingPoint = false;
            listsDirty = true;
            RefreshInfo();
            UpdateCompareUI();
            FrameScan();

            int saved = doc.evidence.Count + doc.measurements.Count;
            Toast(saved > 0
                ? $"Loaded {Path.GetFileName(slot.FilePath)} with {saved} saved annotation{(saved == 1 ? "" : "s")}."
                : $"Loaded {Path.GetFileName(slot.FilePath)} ({slot.SplatCount:N0} splats).");
        }

        void OnScanBLoaded(ScanSlot slot)
        {
            scanB.Flipped = scanA.Flipped;
            compareChoose.text = Path.GetFileName(slot.FilePath);
            if (compareView == CompareView.A) compareView = CompareView.Both;
            UpdateCompareUI();
            Toast($"Comparison scan loaded: {Path.GetFileName(slot.FilePath)}");
        }

        void OnScanFailed(ScanSlot slot, string error)
        {
            Toast("Couldn't load the scan: " + error, 8f);
        }

        void UpdateLoadingUI()
        {
            bool loading = scanA.IsLoading || scanB.IsLoading;
            ScanSlot s = scanA.IsLoading ? scanA : scanB;
            loadingPanel.style.display = loading ? DisplayStyle.Flex : DisplayStyle.None;
            if (loading)
            {
                loadingLabel.text = $"{s.LoadStage}...  {Mathf.RoundToInt(s.LoadProgress * 100f)}%";
                progressFill.style.width = Length.Percent(Mathf.Clamp01(s.LoadProgress) * 100f);
            }
            emptyState.style.display = !scanA.HasScan && !scanA.IsLoading ? DisplayStyle.Flex : DisplayStyle.None;
            toolHint.style.display = scanA.HasScan && !loading ? DisplayStyle.Flex : DisplayStyle.None;
            exportButton.EnableInClassList("disabled", !scanA.HasScan || exporting);
        }

        void FrameScan()
        {
            if (!scanA.HasScan || viewer == null) return;
            Bounds b = scanA.CropEnabled ? scanA.TransformBounds(scanA.CropLocal) : scanA.RobustWorldBounds;
            viewer.Frame(b);
        }

        void SetFlipped(bool value)
        {
            scanA.Flipped = value;
            scanB.Flipped = value;
            if (doc != null)
            {
                doc.flipped = value;
                MarkDocDirty();
            }
            listsDirty = true;
            FrameScan();
        }

        // ======================================================================
        // Evidence and measurements
        // ======================================================================

        void AddEvidence(Vector3 local)
        {
            var e = new EvidenceItem { number = doc.NextEvidenceNumber(), localPosition = local };
            e.title = $"Evidence {e.number:00}";
            doc.evidence.Add(e);
            MarkDocDirty();
            SetSideTab(true);
            RebuildLists();
            SelectEvidence(e, false);
            titleField.schedule.Execute(() =>
            {
                titleField.Focus();
                titleField.SelectAll();
            });
        }

        void AddMeasurement(Vector3 a, Vector3 b)
        {
            var m = new Measurement { number = doc.NextMeasurementNumber(), localA = a, localB = b };
            doc.measurements.Add(m);
            MarkDocDirty();
            SetSideTab(true);
            RebuildLists();
            SelectMeasurement(m, false);
        }

        void SelectEvidence(EvidenceItem e, bool focusCamera)
        {
            selectedEvidence = e;
            selectedMeasurement = null;
            UpdateSelectionUI();
            if (focusCamera && e != null) viewer.FocusOn(ToWorld(e.localPosition));
        }

        void SelectMeasurement(Measurement m, bool focusCamera)
        {
            selectedMeasurement = m;
            selectedEvidence = null;
            UpdateSelectionUI();
            if (focusCamera && m != null) viewer.FocusOn(ToWorld((m.localA + m.localB) * 0.5f));
        }

        void Deselect()
        {
            if (selectedEvidence == null && selectedMeasurement == null) return;
            selectedEvidence = null;
            selectedMeasurement = null;
            UpdateSelectionUI();
        }

        void DeleteSelected()
        {
            if (doc == null) return;
            if (selectedEvidence != null)
            {
                doc.evidence.Remove(selectedEvidence);
                Toast($"Deleted evidence {selectedEvidence.number:00}.");
            }
            else if (selectedMeasurement != null)
            {
                doc.measurements.Remove(selectedMeasurement);
                Toast($"Deleted measurement M{selectedMeasurement.number}.");
            }
            else return;

            selectedEvidence = null;
            selectedMeasurement = null;
            MarkDocDirty();
            listsDirty = true;
        }

        void UpdateSelectionUI()
        {
            foreach (var kv in rowFor)
                kv.Value.EnableInClassList("selected", kv.Key == (object)selectedEvidence || kv.Key == (object)selectedMeasurement);

            bool show = selectedEvidence != null && evidenceTabActive;
            detailsPanel.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (show)
            {
                detailsBadge.text = selectedEvidence.number.ToString("00", Inv);
                detailsCoords.text = FormatCoords(ToWorld(selectedEvidence.localPosition));
                titleField.SetValueWithoutNotify(selectedEvidence.title);
                notesField.SetValueWithoutNotify(selectedEvidence.notes);
            }
        }

        void RebuildLists()
        {
            evidenceList.Clear();
            measureList.Clear();
            rowFor.Clear();
            titleLabelFor.Clear();

            bool hasScan = scanA.HasScan && doc != null;
            evidenceEmpty.text = hasScan
                ? "No evidence logged yet. Choose Annotate and click on the scan to place a marker."
                : "Open a scan to start logging evidence.";
            evidenceEmpty.style.display = hasScan && doc.evidence.Count > 0 ? DisplayStyle.None : DisplayStyle.Flex;
            measureEmpty.style.display = hasScan && doc.measurements.Count > 0 ? DisplayStyle.None : DisplayStyle.Flex;

            if (hasScan)
            {
                foreach (var e in doc.evidence)
                {
                    var item = e;
                    var row = UIBuild.Div(evidenceList, "evidence-row");
                    UIBuild.Text(row, item.number.ToString("00", Inv), "badge");
                    var text = UIBuild.Div(row, "evidence-text");
                    titleLabelFor[item] = UIBuild.Text(text, item.title, "evidence-title");
                    UIBuild.Text(text, FormatCoords(ToWorld(item.localPosition)), "evidence-coords");
                    row.RegisterCallback<ClickEvent>(_ => SelectEvidence(item, true));
                    rowFor[item] = row;
                }

                foreach (var m in doc.measurements)
                {
                    var item = m;
                    var row = UIBuild.Div(measureList, "measure-row");
                    UIBuild.Text(row, "M" + item.number.ToString(Inv), "measure-tag");
                    UIBuild.Text(row, FormatLength(item.RawLength), "measure-length");
                    var del = UIBuild.Div(row, "icon-button", "btn");
                    del.Add(new VectorIcon(IconKind.Trash, 16f));
                    del.RegisterCallback<ClickEvent>(evt =>
                    {
                        evt.StopPropagation();
                        selectedMeasurement = item;
                        selectedEvidence = null;
                        DeleteSelected();
                    });
                    row.RegisterCallback<ClickEvent>(_ => SelectMeasurement(item, true));
                    rowFor[item] = row;
                }
            }

            UpdateSelectionUI();
            RefreshInfo();
        }

        string FormatCoords(Vector3 world)
        {
            Vector3 w = world * Mpu;
            return string.Format(Inv, "X {0:0.00}, Y {1:0.00}, Z {2:0.00} m", w.x, w.y, w.z);
        }

        string FormatLength(float rawLength)
        {
            float metres = rawLength * Mpu;
            return metres < 0.1f
                ? (metres * 100f).ToString("0.0", Inv) + " cm"
                : metres.ToString("0.00", Inv) + " m";
        }

        // ======================================================================
        // Overlay: markers, measurement lines, crop box
        // ======================================================================

        void UpdateMarkers()
        {
            int markers = 0, labels = 0;
            bool show = doc != null && scanA.HasScan && scanA.Visible && viewer != null;

            if (show)
            {
                foreach (var e in doc.evidence)
                {
                    if (!WorldToViewport(ToWorld(e.localPosition), out Vector2 p)) continue;
                    var m = Pooled(markerPool, markers++, "marker");
                    m.text = e.number.ToString("00", Inv);
                    m.userData = e;
                    m.style.left = p.x;
                    m.style.top = p.y;
                    m.EnableInClassList("marker-selected", e == selectedEvidence);
                }

                foreach (var ms in doc.measurements)
                {
                    if (!WorldToViewport(ToWorld((ms.localA + ms.localB) * 0.5f), out Vector2 p)) continue;
                    var l = Pooled(measurePool, labels++, "measure-label");
                    l.text = FormatLength(ms.RawLength);
                    l.userData = ms;
                    l.style.left = p.x;
                    l.style.top = p.y;
                    l.EnableInClassList("selected", ms == selectedMeasurement);
                }
            }

            for (int i = markers; i < markerPool.Count; i++) markerPool[i].style.display = DisplayStyle.None;
            for (int i = labels; i < measurePool.Count; i++) measurePool[i].style.display = DisplayStyle.None;
        }

        Label Pooled(List<Label> pool, int index, string cls)
        {
            while (pool.Count <= index)
            {
                var label = new Label();
                label.AddToClassList(cls);
                label.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    if (label.userData is EvidenceItem e) { SetSideTab(true); SelectEvidence(e, false); }
                    else if (label.userData is Measurement m) { SetSideTab(true); SelectMeasurement(m, false); }
                    evt.StopPropagation();
                });
                markerLayer.Add(label);
                pool.Add(label);
            }
            var l = pool[index];
            l.style.display = DisplayStyle.Flex;
            return l;
        }

        void DrawOverlay(Painter2D p)
        {
            if (doc == null || !scanA.HasScan || viewer == null || !scanA.Visible) return;

            foreach (var m in doc.measurements)
            {
                if (!WorldToViewport(ToWorld(m.localA), out Vector2 a) || !WorldToViewport(ToWorld(m.localB), out Vector2 b)) continue;
                Color c = m == selectedMeasurement ? MeasureRedSelected : MeasureRed;
                DrawDashed(p, a, b, c, 2.5f);
                DrawDot(p, a, 4.5f, c);
                DrawDot(p, b, 4.5f, c);
            }

            if (tool == Tool.Measure && hasPendingPoint && WorldToViewport(ToWorld(pendingLocal), out Vector2 start))
            {
                DrawDot(p, start, 5f, MeasureRed);
                if (pointerInViewport)
                {
                    Color faded = MeasureRed;
                    faded.a = 0.6f;
                    DrawDashed(p, start, pointerLocal, faded, 2f);
                }
            }

            if (tool == Tool.Crop) DrawCropBox(p);
        }

        void DrawCropBox(Painter2D p)
        {
            Bounds box = scanA.CropLocal;
            Vector3 c = box.center, e = box.extents;
            var pts = new Vector2[8];
            for (int i = 0; i < 8; i++)
            {
                var corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                if (!WorldToViewport(ToWorld(corner), out pts[i])) return;
            }

            Color col = CropTeal;
            if (!scanA.CropEnabled) col.a = 0.45f;
            p.strokeColor = col;
            p.lineWidth = 1.5f;
            p.BeginPath();
            for (int i = 0; i < 8; i++)
            {
                for (int bit = 1; bit < 8; bit <<= 1)
                {
                    int j = i | bit;
                    if (j == i) continue;
                    p.MoveTo(pts[i]);
                    p.LineTo(pts[j]);
                }
            }
            p.Stroke();
        }

        static void DrawDashed(Painter2D p, Vector2 a, Vector2 b, Color c, float width, float dash = 9f, float gap = 6f)
        {
            float len = Vector2.Distance(a, b);
            if (len < 0.5f || len > 20000f) return;
            Vector2 dir = (b - a) / len;
            p.strokeColor = c;
            p.lineWidth = width;
            p.lineCap = LineCap.Butt;
            p.BeginPath();
            for (float t = 0f; t < len; t += dash + gap)
            {
                p.MoveTo(a + dir * t);
                p.LineTo(a + dir * Mathf.Min(t + dash, len));
            }
            p.Stroke();
        }

        static void DrawDot(Painter2D p, Vector2 c, float radius, Color color)
        {
            p.fillColor = color;
            p.BeginPath();
            p.Arc(c, radius, Angle.Degrees(0f), Angle.Degrees(360f), ArcDirection.Clockwise);
            p.ClosePath();
            p.Fill();
        }

        // ======================================================================
        // Crop
        // ======================================================================

        /// <summary>The slider range: the scan's main body, padded by 25% on every side.</summary>
        Bounds CropRange()
        {
            Bounds b = scanA.RobustLocalBounds;
            b.Expand(b.size * 0.5f);
            return b;
        }

        void ApplyCropFromUI()
        {
            if (!scanA.HasScan) return;
            Bounds r = CropRange();
            Vector2 x = cropX.value, y = cropY.value, z = cropZ.value;
            var min = new Vector3(Mathf.Lerp(r.min.x, r.max.x, x.x), Mathf.Lerp(r.min.y, r.max.y, y.x), Mathf.Lerp(r.min.z, r.max.z, z.x));
            var max = new Vector3(Mathf.Lerp(r.min.x, r.max.x, x.y), Mathf.Lerp(r.min.y, r.max.y, y.y), Mathf.Lerp(r.min.z, r.max.z, z.y));
            var box = new Bounds();
            box.SetMinMax(min, max);
            scanA.SetCrop(cropToggle.value, box);

            if (doc != null)
            {
                doc.hasCropBox = true;
                doc.cropEnabled = cropToggle.value;
                doc.cropMin = min;
                doc.cropMax = max;
                MarkDocDirty();
            }
        }

        void ApplyCropFromDoc()
        {
            Bounds r = CropRange();
            float Inv01(float a, float b, float v) => Mathf.Clamp01(Mathf.InverseLerp(a, b, v));
            const float lo = 1f / 6f, hi = 5f / 6f; // the unpadded scan body inside the padded range

            if (doc != null && doc.hasCropBox)
            {
                cropX.SetValueWithoutNotify(new Vector2(Inv01(r.min.x, r.max.x, doc.cropMin.x), Inv01(r.min.x, r.max.x, doc.cropMax.x)));
                cropY.SetValueWithoutNotify(new Vector2(Inv01(r.min.y, r.max.y, doc.cropMin.y), Inv01(r.min.y, r.max.y, doc.cropMax.y)));
                cropZ.SetValueWithoutNotify(new Vector2(Inv01(r.min.z, r.max.z, doc.cropMin.z), Inv01(r.min.z, r.max.z, doc.cropMax.z)));
                cropToggle.SetValueWithoutNotify(doc.cropEnabled);
                var box = new Bounds();
                box.SetMinMax(doc.cropMin, doc.cropMax);
                scanA.SetCrop(doc.cropEnabled, box);
            }
            else
            {
                cropX.SetValueWithoutNotify(new Vector2(lo, hi));
                cropY.SetValueWithoutNotify(new Vector2(lo, hi));
                cropZ.SetValueWithoutNotify(new Vector2(lo, hi));
                cropToggle.SetValueWithoutNotify(false);
                scanA.SetCrop(false, scanA.RobustLocalBounds);
            }
        }

        void ResetCropBox()
        {
            const float lo = 1f / 6f, hi = 5f / 6f;
            cropX.SetValueWithoutNotify(new Vector2(lo, hi));
            cropY.SetValueWithoutNotify(new Vector2(lo, hi));
            cropZ.SetValueWithoutNotify(new Vector2(lo, hi));
            ApplyCropFromUI();
        }

        // ======================================================================
        // Compare
        // ======================================================================

        void SetCompareView(CompareView v)
        {
            compareView = v;
            UpdateCompareUI();
        }

        void UpdateCompareUI()
        {
            foreach (var kv in compareButtons) kv.Value?.EnableInClassList("active", kv.Key == compareView);
            if (!scanB.HasScan) compareChoose.text = "Choose scan...";

            if (mode == Mode.Compare)
            {
                scanA.Visible = compareView != CompareView.B;
                scanB.Visible = scanB.HasScan && compareView != CompareView.A;
            }
            else
            {
                scanA.Visible = true;
                scanB.Visible = false;
            }
        }

        // ======================================================================
        // Side panel and info
        // ======================================================================

        void SetSideTab(bool evidence)
        {
            evidenceTabActive = evidence;
            tabEvidence.EnableInClassList("active", evidence);
            tabInfo.EnableInClassList("active", !evidence);
            evidencePage.style.display = evidence ? DisplayStyle.Flex : DisplayStyle.None;
            infoPage.style.display = evidence ? DisplayStyle.None : DisplayStyle.Flex;
            if (evidence) UpdateSelectionUI();
            else detailsPanel.style.display = DisplayStyle.None;
        }

        void RefreshInfo()
        {
            bool has = scanA.HasScan;
            SetInfo("File", has ? Path.GetFileName(scanA.FilePath) : "-");
            SetInfo("Folder", has ? Path.GetDirectoryName(scanA.FilePath) : "-");
            SetInfo("File size", has ? FormatBytes(scanA.FileBytes) : "-");
            SetInfo("Splats", has ? scanA.SplatCount.ToString("N0", Inv) : "-");
            SetInfo("SH bands", has ? scanA.SHBands.ToString(Inv) : "-");
            if (has)
            {
                Vector3 size = scanA.RobustLocalBounds.size * Mpu;
                SetInfo("Extent", string.Format(Inv, "{0:0.00} × {1:0.00} × {2:0.00} m", size.x, size.y, size.z));
            }
            else SetInfo("Extent", "-");
            SetInfo("Load time", has ? scanA.LoadSeconds.ToString("0.0", Inv) + " s" : "-");
            SetInfo("Annotations", doc != null && has
                ? $"{doc.evidence.Count} evidence, {doc.measurements.Count} measurements"
                : "-");
            scaleLabel.text = "1 scan unit = " + Mpu.ToString("0.####", Inv) + " m";
        }

        static string FormatBytes(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0", Inv) + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0", Inv) + " MB";
            return (bytes / 1024.0).ToString("0", Inv) + " KB";
        }

        void SetInfo(string key, string value)
        {
            if (infoValues.TryGetValue(key, out var label))
            {
                label.text = value;
                label.tooltip = value;
            }
        }

        void Calibrate()
        {
            if (doc == null) { Toast("Open a scan first."); return; }
            if (selectedMeasurement == null)
            {
                Toast("Select a measurement in the Evidence tab first, then press Calibrate.");
                return;
            }
            float real = calibLength.value;
            if (real <= 0f) { Toast("Enter a real length greater than zero."); return; }
            float raw = selectedMeasurement.RawLength;
            if (raw < 1e-6f) { Toast("That measurement is too short to calibrate from."); return; }

            doc.metresPerUnit = real / raw;
            MarkDocDirty();
            listsDirty = true;
            RefreshInfo();
            Toast($"Scale calibrated: M{selectedMeasurement.number} is now {real.ToString("0.00", Inv)} m.");
        }

        // ======================================================================
        // Saving and export
        // ======================================================================

        void MarkDocDirty()
        {
            docDirty = true;
            lastEditTime = Time.unscaledTime;
        }

        void SaveDoc()
        {
            docDirty = false;
            if (doc == null) return;
            if (!doc.Save(out string error)) Toast("Couldn't save annotations: " + error, 8f);
        }

        IEnumerator ExportRoutine()
        {
            if (!scanA.HasScan || doc == null) { Toast("Open a scan before exporting."); yield break; }
            exporting = true;
            if (docDirty) SaveDoc();

            string dir = CreateExportFolder();
            if (dir == null)
            {
                exporting = false;
                Toast("Couldn't create an export folder.", 8f);
                yield break;
            }

            // Hide the panels so the screenshot is just the annotated scan.
            app.AddToClassList("capture-mode");
            yield return null;
            yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
            app.RemoveFromClassList("capture-mode");

            try
            {
                File.WriteAllBytes(Path.Combine(dir, "screenshot.png"), shot.EncodeToPNG());
                File.WriteAllText(Path.Combine(dir, "report.json"), JsonUtility.ToJson(BuildReport(), true));
                File.WriteAllText(Path.Combine(dir, "evidence.csv"), BuildCsv(), Encoding.UTF8);
                string folder = dir;
                Toast("Exported to " + dir, 10f, "Show folder", () => Application.OpenURL(new Uri(folder).AbsoluteUri));
            }
            catch (Exception e)
            {
                Toast("Export failed: " + e.Message, 8f);
            }
            finally
            {
                Destroy(shot);
                exporting = false;
            }
        }

        string CreateExportFolder()
        {
            string name = Path.GetFileNameWithoutExtension(scanA.FilePath) + "_export_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv);
            string[] candidates =
            {
                Path.Combine(Path.GetDirectoryName(scanA.FilePath) ?? "", name),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ForensicNeRF", "Exports", name)
            };
            foreach (var c in candidates)
            {
                try
                {
                    Directory.CreateDirectory(c);
                    return c;
                }
                catch (Exception) { /* try the next location */ }
            }
            return null;
        }

        ExportReport BuildReport()
        {
            var report = new ExportReport
            {
                exportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv),
                exportedBy = userName,
                scanFile = Path.GetFileName(scanA.FilePath),
                scanPath = scanA.FilePath,
                splatCount = scanA.SplatCount,
                metresPerUnit = Mpu
            };
            foreach (var e in doc.evidence)
            {
                report.evidence.Add(new ExportedEvidence
                {
                    number = e.number,
                    title = e.title,
                    notes = e.notes,
                    positionMetres = ToWorld(e.localPosition) * Mpu
                });
            }
            foreach (var m in doc.measurements)
            {
                report.measurements.Add(new ExportedMeasurement
                {
                    number = m.number,
                    startMetres = ToWorld(m.localA) * Mpu,
                    endMetres = ToWorld(m.localB) * Mpu,
                    lengthMetres = m.RawLength * Mpu
                });
            }
            return report;
        }

        string BuildCsv()
        {
            string Esc(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            var sb = new StringBuilder();
            sb.AppendLine("type,number,title,notes,x_m,y_m,z_m,length_m");
            foreach (var e in doc.evidence)
            {
                Vector3 p = ToWorld(e.localPosition) * Mpu;
                sb.AppendLine(string.Format(Inv, "evidence,{0},{1},{2},{3:0.000},{4:0.000},{5:0.000},",
                    e.number, Esc(e.title), Esc(e.notes), p.x, p.y, p.z));
            }
            foreach (var m in doc.measurements)
            {
                Vector3 mid = ToWorld((m.localA + m.localB) * 0.5f) * Mpu;
                sb.AppendLine(string.Format(Inv, "measurement,{0},{1},,{2:0.000},{3:0.000},{4:0.000},{5:0.000}",
                    m.number, Esc("M" + m.number), mid.x, mid.y, mid.z, m.RawLength * Mpu));
            }
            return sb.ToString();
        }

        // ======================================================================
        // Toast and user menu
        // ======================================================================

        void Toast(string message, float seconds = 4f, string actionText = null, Action action = null)
        {
            toastLabel.text = message;
            toastCallback = action;
            toastAction.text = actionText ?? "";
            toastAction.style.display = action != null ? DisplayStyle.Flex : DisplayStyle.None;
            toast.style.display = DisplayStyle.Flex;
            toastTimer?.Pause();
            toastTimer = toast.schedule.Execute(() => toast.style.display = DisplayStyle.None)
                .StartingIn((long)(seconds * 1000f));
        }

        void ToggleUserMenu()
        {
            if (menuBackdrop != null) { CloseUserMenu(); return; }

            menuBackdrop = UIBuild.Div(root, "menu-backdrop");
            menuBackdrop.RegisterCallback<PointerDownEvent>(evt => { if (evt.target == menuBackdrop) CloseUserMenu(); });
            var menu = UIBuild.Div(menuBackdrop, "menu");
            UIBuild.Text(menu, userName, "menu-header");
            MenuItem(menu, "Keyboard shortcuts", () => Toast(
                "F frame scan  ·  Esc cancel / deselect  ·  Delete remove selected  ·  Double-click set orbit centre  ·  Shift-drag pan", 8f));
            MenuItem(menu, "Frame scan", FrameScan);
            MenuItem(menu, "Open scan...", OpenScanPicker);
            MenuItem(menu, "Quit", Quit);
        }

        void MenuItem(VisualElement menu, string text, Action action)
        {
            UIBuild.TextButton(menu, text, () =>
            {
                root.schedule.Execute(() =>
                {
                    CloseUserMenu();
                    action();
                });
            }, "menu-item");
        }

        void CloseUserMenu()
        {
            menuBackdrop?.RemoveFromHierarchy();
            menuBackdrop = null;
        }

        void Quit()
        {
            if (docDirty) SaveDoc();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
