using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace ForensicNeRF
{
    /// <summary>
    /// In-app file picker styled to match the rest of the UI. Works in builds on every
    /// desktop platform without native plugins.
    /// Click a folder to open it, click a file to select it, double-click to open it.
    /// </summary>
    public class FilePickerModal
    {
        const string PrefsKey = "ForensicNeRF.LastFolder";

        public bool IsOpen => backdrop.parent != null;

        readonly VisualElement host;
        readonly VisualElement backdrop;
        readonly Label titleLabel;
        readonly TextField pathField;
        readonly ScrollView list;
        readonly Label status;
        readonly Label openButton;
        readonly string[] extensions;

        string currentFolder;
        string selectedFile;
        VisualElement selectedRow;
        Action<string> onPicked;

        public FilePickerModal(VisualElement host, string[] extensions)
        {
            this.host = host;
            this.extensions = extensions;

            backdrop = UIBuild.Div(null, "modal-backdrop");
            backdrop.RegisterCallback<PointerDownEvent>(e => { if (e.target == backdrop) Close(); });

            var modal = UIBuild.Div(backdrop, "modal");

            var header = UIBuild.Div(modal, "modal-header");
            titleLabel = UIBuild.Text(header, "Open scan", "modal-title");
            UIBuild.Div(header, "flex-spacer");
            UIBuild.IconButton(header, IconKind.Close, 16f, Close, "icon-button");

            var pathRow = UIBuild.Div(modal, "modal-path-row");
            pathField = new TextField();
            pathField.AddToClassList("path-field");
            pathRow.Add(pathField);
            pathField.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    NavigateOrOpen(pathField.value);
                    e.StopPropagation();
                }
            }, TrickleDown.TrickleDown);
            UIBuild.TextButton(pathRow, "Go", () => NavigateOrOpen(pathField.value), "chip");

            var quick = UIBuild.Div(modal, "modal-quick-row");
            var up = UIBuild.Div(quick, "chip", "btn");
            up.Add(new VectorIcon(IconKind.Up, 14f));
            UIBuild.Text(up, "Up", "chip-text");
            up.AddManipulator(new Clickable(GoUp));
            UIBuild.TextButton(quick, "Home", () => Navigate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "chip");
            UIBuild.TextButton(quick, "Desktop", () => Navigate(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)), "chip");
            UIBuild.TextButton(quick, "Documents", () => Navigate(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)), "chip");
            try
            {
                foreach (var drive in Directory.GetLogicalDrives())
                {
                    string d = drive;
                    string label = d.TrimEnd('\\', '/');
                    if (label.Length == 0) label = "/";
                    UIBuild.TextButton(quick, label, () => Navigate(d), "chip");
                }
            }
            catch (Exception) { /* drive listing isn't available everywhere */ }

            list = new ScrollView(ScrollViewMode.Vertical);
            list.AddToClassList("modal-list");
            modal.Add(list);

            status = UIBuild.Text(modal, "", "modal-status");

            var footer = UIBuild.Div(modal, "modal-footer");
            UIBuild.Text(footer, "Gaussian splats: " + string.Join(", ", extensions), "modal-hint");
            UIBuild.Div(footer, "flex-spacer");
            UIBuild.TextButton(footer, "Cancel", Close, "ghost-button");
            openButton = UIBuild.TextButton(footer, "Open", OpenSelected, "primary-button");
        }

        public void Show(string title, Action<string> picked)
        {
            titleLabel.text = title;
            onPicked = picked;
            SetSelection(null, null);
            list.Clear();
            status.text = "";
            if (!IsOpen) host.Add(backdrop);
            backdrop.BringToFront();
            Debug.Log("[ForensicNeRF] File picker opened.");

            string saved = PlayerPrefs.GetString(PrefsKey, "");
            Navigate(Directory.Exists(saved) ? saved : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        }

        public void Close() => backdrop.RemoveFromHierarchy();

        void GoUp()
        {
            if (string.IsNullOrEmpty(currentFolder)) return;
            var parent = Directory.GetParent(currentFolder);
            if (parent != null) Navigate(parent.FullName);
        }

        void NavigateOrOpen(string path)
        {
            if (File.Exists(path)) { selectedFile = path; OpenSelected(); }
            else Navigate(path);
        }

        /// <summary>Changes folder on the next UI tick, so the list is never rebuilt mid-click.</summary>
        // Scheduled on the host, which is never removed. Work scheduled on the picker itself
        // would be paused when the picker closes and then replayed the next time it opens.
        void Navigate(string folder) => host.schedule.Execute(() => { if (IsOpen) Rebuild(folder); });

        void Rebuild(string folder)
        {
            var folders = new List<DirectoryInfo>();
            var files = new List<FileInfo>();
            try
            {
                folder = Path.GetFullPath(folder);
                var dir = new DirectoryInfo(folder);
                if (!dir.Exists) { status.text = "Folder not found."; return; }

                foreach (var d in dir.GetDirectories())
                {
                    if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    folders.Add(d);
                }
                foreach (var f in dir.GetFiles())
                {
                    if (Array.IndexOf(extensions, f.Extension.ToLowerInvariant()) >= 0) files.Add(f);
                }
            }
            catch (UnauthorizedAccessException) { status.text = "Access denied."; return; }
            catch (Exception e) { status.text = e.Message; return; }

            folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            currentFolder = folder;
            pathField.SetValueWithoutNotify(folder);
            PlayerPrefs.SetString(PrefsKey, folder);
            SetSelection(null, null);
            list.Clear();
            list.scrollOffset = Vector2.zero;

            foreach (var d in folders)
            {
                string full = d.FullName;
                var row = MakeRow(IconKind.Folder, d.Name, "", "folder-row");
                row.RegisterCallback<ClickEvent>(_ => Navigate(full));
                list.Add(row);
            }
            foreach (var f in files)
            {
                string full = f.FullName;
                string meta = FormatSize(f.Length) + "   " + f.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                var row = MakeRow(IconKind.File, f.Name, meta, "file-row");
                row.RegisterCallback<ClickEvent>(e =>
                {
                    SetSelection(row, full);
                    if (e.clickCount >= 2) OpenSelected();
                });
                list.Add(row);
            }

            status.text = folders.Count == 0 && files.Count == 0 ? "No folders or splat files here." : "";
        }

        static VisualElement MakeRow(IconKind icon, string name, string meta, string kindClass)
        {
            var row = UIBuild.Div(null, "picker-row", kindClass);
            row.Add(new VectorIcon(icon, 18f));
            UIBuild.Text(row, name, "picker-name");
            UIBuild.Text(row, meta, "picker-meta");
            return row;
        }

        void SetSelection(VisualElement row, string file)
        {
            selectedRow?.RemoveFromClassList("selected");
            selectedRow = row;
            selectedFile = file;
            selectedRow?.AddToClassList("selected");
            openButton.EnableInClassList("disabled", file == null);
        }

        void OpenSelected()
        {
            if (string.IsNullOrEmpty(selectedFile)) return;
            string file = selectedFile;
            var callback = onPicked;
            onPicked = null;
            SetSelection(null, null);
            Close();
            host.schedule.Execute(() => callback?.Invoke(file));
        }

        static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
        }
    }
}
