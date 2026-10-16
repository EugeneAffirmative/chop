using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Chop
{
    /// <summary>
    /// Export: the file is saved under exactly the name typed (the extension is added if left off).
    /// Names that are taken or that Windows won't accept are flagged in red and can't be used; the
    /// clip's own name is only allowed with "Replace the original clip" ticked. Enter exports, Esc cancels.
    /// </summary>
    class ExportDialog : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public string OutputPath { get; private set; }
        public bool ReplaceOriginal { get { return replaceBox.Checked; } }

        readonly float f;
        readonly string source, ext;
        string folder;
        readonly TextBox nameBox;
        readonly Label extLbl, errorLbl, folderLbl, noteLbl;
        readonly CheckBox replaceBox;
        readonly FlatButton okBtn, cancelBtn, browseBtn;
        readonly ComboBox resBox;
        readonly System.Collections.Generic.List<int> heights = new System.Collections.Generic.List<int>();
        readonly string length, modeText, codecLabel;

        /// <summary>Short side to scale to (720 = 720p), or 0 for the original size.</summary>
        public int TargetHeight { get { return resBox.SelectedIndex > 0 ? heights[resBox.SelectedIndex - 1] : 0; } }

        void UpdateNote()
        {
            int h = TargetHeight;
            noteLbl.Text = length + "  ·  " + (h == 0 ? modeText : "re-encoded to " + h + "p (" + codecLabel + "), so it takes longer");
        }
        static readonly Color ErrorRed = Color.FromArgb(255, 105, 105);
        static readonly string[] MediaExts = { ".mp4", ".mkv", ".mov", ".m4v", ".webm", ".avi", ".ts", ".m2ts", ".mts", ".flv", ".wmv",
            ".mpg", ".mpeg", ".3gp", ".mp3", ".m4a", ".wav", ".flac", ".ogg", ".opus", ".mka", ".aac" };

        public ExportDialog(float scale, string sourcePath, string ext, string length, string modeText, int shortSide, string codecLabel)
        {
            f = scale;
            source = sourcePath;
            this.ext = ext;
            folder = Path.GetDirectoryName(sourcePath);

            Text = "Export";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bar;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9.5f);
            KeyPreview = true;
            int pad = (int)(16 * f), w = (int)(520 * f), y = pad;
            ClientSize = new Size(w, (int)((shortSide > 0 ? 298 : 258) * f));
            this.length = length; this.modeText = modeText; this.codecLabel = codecLabel;

            var nameLbl = new Label { Text = "File name", AutoSize = true, ForeColor = Theme.Dim, Location = new Point(pad, y) };
            y += (int)(22 * f);
            extLbl = new Label { Text = ext, AutoSize = true, ForeColor = Theme.Dim };
            nameBox = new TextBox
            {
                Text = Path.GetFileNameWithoutExtension(sourcePath), BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(38, 38, 45), ForeColor = Theme.Text, Font = new Font("Segoe UI", 11f), Location = new Point(pad, y)
            };
            nameBox.Width = w - 2 * pad - (int)(52 * f);
            nameBox.TextChanged += (s, e) => CheckName();
            extLbl.Location = new Point(nameBox.Right + (int)(6 * f), y + (nameBox.Height - extLbl.PreferredHeight) / 2);
            y += nameBox.Height + (int)(3 * f);

            errorLbl = new Label { AutoSize = false, ForeColor = ErrorRed, Location = new Point(pad, y), Size = new Size(w - 2 * pad, (int)(20 * f)) };
            y += errorLbl.Height + (int)(4 * f);

            browseBtn = new FlatButton(f, "Change…", null);
            browseBtn.Click += (s, e) => Browse();
            folderLbl = new Label { AutoSize = false, ForeColor = Theme.Dim, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            folderLbl.Bounds = new Rectangle(pad, y, w - 2 * pad - browseBtn.Width - (int)(8 * f), browseBtn.Height);
            browseBtn.Location = new Point(w - pad - browseBtn.Width, y);
            y += browseBtn.Height + (int)(12 * f);

            // Resolution: Original keeps the stream-copy export; a smaller size re-encodes the kept parts
            var resLbl = new Label { Text = "Resolution", AutoSize = true, ForeColor = Theme.Dim };
            resBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(38, 38, 45),
                ForeColor = Theme.Text, Width = (int)(230 * f), Location = new Point(pad + (int)(86 * f), y)
            };
            resBox.Items.Add("Original (no re-encode)");
            foreach (int h in new[] { 2160, 1440, 1080, 720, 480, 360 })
                if (shortSide > h + 8) { resBox.Items.Add(h + "p"); heights.Add(h); }
            resBox.SelectedIndex = 0;
            resBox.SelectedIndexChanged += (s, e) => UpdateNote();
            resLbl.Location = new Point(pad, y + (resBox.Height - resLbl.PreferredHeight) / 2);
            bool hasVideo = shortSide > 0;
            resLbl.Visible = resBox.Visible = hasVideo;
            if (hasVideo) y += resBox.Height + (int)(12 * f);

            // Flat style so our light text colour is used (the system style drew it dark grey on dark)
            replaceBox = new CheckBox
            {
                Text = "Replace the original clip (the original goes to the Recycle Bin)",
                AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Bar, Location = new Point(pad, y), FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            replaceBox.FlatAppearance.BorderColor = Color.FromArgb(150, 150, 162);
            replaceBox.FlatAppearance.CheckedBackColor = Theme.Accent;
            replaceBox.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 48, 56);
            replaceBox.CheckedChanged += (s, e) => { okBtn.Text = replaceBox.Checked ? "Export & replace" : "Export"; PlaceButtons(); CheckName(); nameBox.Focus(); };
            y += replaceBox.PreferredSize.Height + (int)(8 * f);

            noteLbl = new Label { AutoSize = false, ForeColor = Theme.Dim, Location = new Point(pad, y), Size = new Size(w - 2 * pad, (int)(20 * f)) };

            okBtn = new FlatButton(f, "Export", "") { Accent = true };
            okBtn.Click += (s, e) => Accept();
            cancelBtn = new FlatButton(f, "Cancel", null);
            cancelBtn.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            PlaceButtons();

            Controls.AddRange(new Control[] { nameLbl, nameBox, extLbl, errorLbl, folderLbl, browseBtn, resLbl, resBox, replaceBox, noteLbl, okBtn, cancelBtn });
            UpdateNote();
            UpdateFolder();
            CheckName();
            Shown += (s, e) => { nameBox.Focus(); nameBox.SelectAll(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1; // dark title bar
            try { DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { }
        }

        void PlaceButtons()
        {
            int pad = (int)(16 * f), by = ClientSize.Height - pad - okBtn.Height;
            okBtn.Location = new Point(ClientSize.Width - pad - okBtn.Width, by);
            cancelBtn.Location = new Point(okBtn.Left - (int)(8 * f) - cancelBtn.Width, by);
        }

        void UpdateFolder() { folderLbl.Text = "Save in  " + folder; }

        /// <summary>The name as typed, minus a trailing copy of the extension we add anyway.</summary>
        string TypedName()
        {
            string name = nameBox.Text;
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - ext.Length);
            return name;
        }

        static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        /// <summary>Why the typed name can't be used, or null if it can.</summary>
        string Problem()
        {
            string name = TypedName();
            if (name.Trim().Length == 0) return "";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "File names can't contain  \\ / : * ? \" < > |";
            if (name.EndsWith(" ") || name.EndsWith(".") || name.StartsWith(" ")) return "File names can't start with a space or end with a space or a dot.";
            if (Array.IndexOf(Reserved, name.Split('.')[0].ToUpperInvariant()) >= 0) return "Windows reserves that name. Try another.";
            string typedExt = Path.GetExtension(name).ToLowerInvariant();
            if (typedExt.Length > 0 && Array.IndexOf(MediaExts, typedExt) >= 0)
                return "This saves as " + ext + ", so leave the extension off.";
            string target = Path.Combine(folder, name + ext);
            if (target.Length >= 260) return "That name is too long for this folder.";
            bool isSource = string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase);
            if (isSource)
                return replaceBox.Checked ? null : "That's the clip you're editing. Tick \"Replace the original clip\" to save over it.";
            if (File.Exists(target) || Directory.Exists(target)) return "\"" + name + ext + "\" already exists here. Pick another name.";
            return null;
        }

        void CheckName()
        {
            string p = Problem();
            errorLbl.Text = p ?? "";
            okBtn.Enabled = p == null;
        }

        void Browse()
        {
            using (var d = new FolderBrowserDialog())
            {
                d.SelectedPath = folder;
                d.Description = "Save the export in…";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                folder = d.SelectedPath;
                UpdateFolder();
                CheckName();
            }
        }

        void Accept()
        {
            if (Problem() != null) { System.Media.SystemSounds.Beep.Play(); return; }
            OutputPath = Path.Combine(folder, TypedName() + ext);
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Enter) { Accept(); return true; }
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // test hooks
        public void SetNameForTest(string name) { nameBox.Text = name; }
        public void SetReplaceForTest(bool on) { replaceBox.Checked = on; }
        public string ErrorText { get { return errorLbl.Text; } }
        public bool CanExport { get { return okBtn.Enabled; } }
        public void SetResolutionForTest(int i) { resBox.SelectedIndex = i; }
        public int ResolutionCount { get { return resBox.Items.Count; } }
    }
}
