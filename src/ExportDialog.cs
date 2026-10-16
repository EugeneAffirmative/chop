using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Chop
{
    /// <summary>
    /// Export: pick the name (and folder), and optionally replace the original clip.
    /// Enter exports, Esc cancels. Anything that would be overwritten goes to the Recycle Bin.
    /// </summary>
    class ExportDialog : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public string OutputPath { get; private set; }
        public bool ReplaceOriginal { get { return replaceBox.Checked; } }

        readonly float f;
        readonly string source, ext, defaultName;
        string folder;
        readonly TextBox nameBox;
        readonly Label extLbl, folderLbl, noteLbl;
        readonly CheckBox replaceBox;
        readonly FlatButton okBtn, cancelBtn, browseBtn;

        public ExportDialog(float scale, string sourcePath, string ext, string summary)
        {
            f = scale;
            source = sourcePath;
            this.ext = ext;
            folder = Path.GetDirectoryName(sourcePath);
            defaultName = FreeName(Path.GetFileNameWithoutExtension(sourcePath) + " (cut)");

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
            ClientSize = new Size(w, (int)(236 * f));

            var nameLbl = new Label { Text = "File name", AutoSize = true, ForeColor = Theme.Dim, Location = new Point(pad, y) };
            y += (int)(22 * f);
            extLbl = new Label { Text = ext, AutoSize = true, ForeColor = Theme.Dim };
            nameBox = new TextBox
            {
                Text = defaultName, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(38, 38, 45), ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 11f), Location = new Point(pad, y)
            };
            nameBox.Width = w - 2 * pad - (int)(52 * f);
            extLbl.Location = new Point(nameBox.Right + (int)(6 * f), y + (nameBox.Height - extLbl.PreferredHeight) / 2);
            y += nameBox.Height + (int)(10 * f);

            browseBtn = new FlatButton(f, "Change…", null);
            browseBtn.Click += (s, e) => Browse();
            folderLbl = new Label { AutoSize = false, ForeColor = Theme.Dim, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            folderLbl.Bounds = new Rectangle(pad, y, w - 2 * pad - browseBtn.Width - (int)(8 * f), browseBtn.Height);
            browseBtn.Location = new Point(w - pad - browseBtn.Width, y);
            y += browseBtn.Height + (int)(12 * f);

            replaceBox = new CheckBox
            {
                Text = "Replace the original clip (the original goes to the Recycle Bin)",
                AutoSize = true, ForeColor = Theme.Text, Location = new Point(pad, y), FlatStyle = FlatStyle.System
            };
            replaceBox.CheckedChanged += (s, e) => OnReplaceChanged();
            y += replaceBox.PreferredSize.Height + (int)(8 * f);

            noteLbl = new Label { Text = summary, AutoSize = false, ForeColor = Theme.Dim, Location = new Point(pad, y), Size = new Size(w - 2 * pad, (int)(20 * f)) };

            okBtn = new FlatButton(f, "Export", "") { Accent = true };
            okBtn.Click += (s, e) => Accept();
            cancelBtn = new FlatButton(f, "Cancel", null);
            cancelBtn.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            int by = ClientSize.Height - pad - okBtn.Height;
            okBtn.Location = new Point(w - pad - okBtn.Width, by);
            cancelBtn.Location = new Point(okBtn.Left - (int)(8 * f) - cancelBtn.Width, by);

            Controls.AddRange(new Control[] { nameLbl, nameBox, extLbl, folderLbl, browseBtn, replaceBox, noteLbl, okBtn, cancelBtn });
            UpdateFolder();
            Shown += (s, e) => { nameBox.Focus(); nameBox.SelectAll(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1; // dark title bar
            try { DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { }
        }

        string FreeName(string stem)
        {
            string n = stem;
            for (int i = 2; File.Exists(Path.Combine(folder, n + ext)); i++) n = stem + " " + i;
            return n;
        }

        void UpdateFolder() { folderLbl.Text = "Save in  " + folder; }

        void OnReplaceChanged()
        {
            // replacing usually means "keep the original's name", so offer it; a typed name is left alone
            string original = Path.GetFileNameWithoutExtension(source);
            bool sameFolder = string.Equals(folder, Path.GetDirectoryName(source), StringComparison.OrdinalIgnoreCase);
            if (replaceBox.Checked && nameBox.Text == defaultName && sameFolder
                && string.Equals(Path.GetExtension(source), ext, StringComparison.OrdinalIgnoreCase))
                nameBox.Text = original;
            else if (!replaceBox.Checked && nameBox.Text == original) nameBox.Text = defaultName;
            okBtn.Text = replaceBox.Checked ? "Export & replace" : "Export";
            okBtn.Location = new Point(ClientSize.Width - (int)(16 * f) - okBtn.Width, okBtn.Top);
            cancelBtn.Location = new Point(okBtn.Left - (int)(8 * f) - cancelBtn.Width, okBtn.Top);
            nameBox.Focus();
            nameBox.SelectionStart = nameBox.Text.Length;
        }

        void Browse()
        {
            using (var d = new SaveFileDialog())
            {
                d.InitialDirectory = folder;
                d.FileName = nameBox.Text + ext;
                d.Filter = ext.TrimStart('.').ToUpperInvariant() + "|*" + ext;
                d.OverwritePrompt = false; // we ask ourselves, and recycle instead of deleting
                if (d.ShowDialog(this) != DialogResult.OK) return;
                folder = Path.GetDirectoryName(d.FileName);
                nameBox.Text = Path.GetFileNameWithoutExtension(d.FileName);
                UpdateFolder();
            }
        }

        void Accept()
        {
            string name = nameBox.Text.Trim();
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - ext.Length);
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show(this, "That name has characters Windows doesn't allow in file names.", "Export");
                return;
            }
            string target = Path.Combine(folder, name + ext);
            bool isSource = string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase);
            if (isSource && !replaceBox.Checked)
            {
                MessageBox.Show(this, "That's the clip you're editing. Tick \"Replace the original clip\" to overwrite it, or pick another name.", "Export");
                return;
            }
            if (!isSource && File.Exists(target) &&
                MessageBox.Show(this, Path.GetFileName(target) + " already exists. Replace it? The old file goes to the Recycle Bin.", "Export",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            OutputPath = target;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Enter) { Accept(); return true; }
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
