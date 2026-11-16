using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ChestCounter
{
    public sealed class ChestEvent
    {
        public double seconds;
        public string action;
        public string kind;
    }

    public sealed class MatchRecord
    {
        public int schema_version;
        public string match_id, recording_id, player, player_id, branch, status;
        public long? timestamp_ms;
        public double duration_seconds, packet_health;
        public int opened, broken, total, estimated, failed_packets, broken_chunks;
        public long packets;
        public string file_name, file_hash, imported_utc;
        public List<ChestEvent> events = new List<ChestEvent>();
        [ScriptIgnore] public string Key { get { return match_id + ":" + (String.IsNullOrEmpty(player_id) ? player : player_id); } }
        [ScriptIgnore] public DateTime Date { get { return timestamp_ms.HasValue ? new DateTime(1970, 1, 1).AddMilliseconds(timestamp_ms.Value) : DateTime.MinValue; } }
        [ScriptIgnore] public string DateText { get { return Date == DateTime.MinValue ? "Date unavailable" : Date.ToString("dd.MM.yyyy  HH:mm"); } }
        public void Validate()
        {
            if ((schema_version < 1 || schema_version > 3) || String.IsNullOrWhiteSpace(match_id) || String.IsNullOrWhiteSpace(player)
                || opened < 0 || broken < 0 || estimated < 0 || opened > 10000 || broken > 10000
                || total != opened + broken || (status != "parsed" && status != "partial")
                || events == null || events.Count > 30000 || packets <= 0 || player.Length > 256 || match_id.Length > 512
                || (timestamp_ms.HasValue && (timestamp_ms.Value < 0 || timestamp_ms.Value > 253402300799000L))
                || Double.IsNaN(packet_health) || packet_health < 0 || packet_health > 1)
                throw new InvalidDataException("Invalid replay analysis data.");
            if (events.Any(e => e == null || Double.IsNaN(e.seconds) || Double.IsInfinity(e.seconds) || e.seconds < 0
                || (e.action != "opened" && e.action != "broken" && e.action != "estimated"))
                || events.Count(e => e.action == "opened") != opened || events.Count(e => e.action == "broken") != broken
                || events.Count(e => e.action == "estimated") != estimated)
                throw new InvalidDataException("Event counts do not match the match summary.");

        }
    }

    public sealed class StatisticsFile
    {
        public int schema_version = 1;
        public List<MatchRecord> matches = new List<MatchRecord>();
    }

    public sealed class ChestProjection
    {
        public readonly int Matches;
        public readonly long Chests;
        public ChestProjection(int matches, long chests)
        {
            if (matches < 0 || chests < 0 || (matches == 0 && chests != 0)) throw new ArgumentOutOfRangeException("matches");
            Matches = matches; Chests = chests;
        }
        public decimal? Average { get { return Matches == 0 ? (decimal?)null : (decimal)Chests / Matches; } }
        public long? ProjectedTotal(int totalMatches)
        {
            if (totalMatches < 0) throw new ArgumentOutOfRangeException("totalMatches");
            return Average.HasValue ? (long?)Decimal.Round(Average.Value * totalMatches, 0, MidpointRounding.AwayFromZero) : null;
        }
    }

    public sealed class StatisticsStore
    {
        public readonly string FilePath;
        public StatisticsFile Data = new StatisticsFile();
        public string RecoveryNotice = "";
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 64 }; }
        public StatisticsStore(string path) { FilePath = path; Load(); }
        private StatisticsFile Read(string path)
        {
            if (new FileInfo(path).Length > 64L * 1024 * 1024) throw new InvalidDataException("The statistics file is too large.");
            StatisticsFile file = Json().Deserialize<StatisticsFile>(File.ReadAllText(path, Encoding.UTF8));
            if (file == null || file.schema_version != 1 || file.matches == null) throw new InvalidDataException("Unknown statistics format.");
            foreach (MatchRecord match in file.matches) match.Validate();
            if (file.matches.GroupBy(m => m.Key).Any(g => g.Count() != 1)) throw new InvalidDataException("The statistics contain duplicate matches.");
            return file;
        }
        private void Load()
        {
            if (!File.Exists(FilePath)) return;
            try { Data = Read(FilePath); }
            catch (Exception first)
            {
                try
                {
                    Data = Read(FilePath + ".bak");
                    string damaged = FilePath + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
                    File.Move(FilePath, damaged);
                    File.Copy(FilePath + ".bak", FilePath);
                    RecoveryNotice = "Statistics restored from backup. The damaged file has been preserved.";
                }
                catch { throw new InvalidDataException("Unable to read statistics. Original files have been preserved: " + FilePath, first); }
            }
        }
        public bool Upsert(MatchRecord record)
        {
            record.Validate();
            int index = Data.matches.FindIndex(m => m.Key == record.Key);
            MatchRecord old = index >= 0 ? Data.matches[index] : null;
            if (old != null) Data.matches[index] = record; else Data.matches.Add(record);
            try { Save(); }
            catch { if (old != null) Data.matches[index] = old; else Data.matches.Remove(record); throw; }
            return old == null;
        }
        public void Remove(MatchRecord record)
        {
            int index = Data.matches.IndexOf(record);
            if (index < 0) return;
            Data.matches.RemoveAt(index);
            try { Save(); } catch { Data.matches.Insert(index, record); throw; }
        }
        public void Clear()
        {
            StatisticsFile previous = Data;
            Data = new StatisticsFile();
            try { Save(); } catch { Data = previous; throw; }
            string directory = Path.GetDirectoryName(FilePath);
            var leftovers = new List<string> { FilePath + ".bak", FilePath + ".tmp" };
            leftovers.AddRange(Directory.GetFiles(directory, Path.GetFileName(FilePath) + ".damaged-*", SearchOption.TopDirectoryOnly));
            var failed = new List<string>();
            foreach (string path in leftovers)
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (IOException) { failed.Add(Path.GetFileName(path)); }
                catch (UnauthorizedAccessException) { failed.Add(Path.GetFileName(path)); }
            }
            if (failed.Count > 0) throw new IOException("Active history was cleared, but these backup files could not be removed: " + String.Join(", ", failed));
        }
        private void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temp = FilePath + ".tmp";
            byte[] bytes = new UTF8Encoding(false).GetBytes(Json().Serialize(Data));
            using (FileStream file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
            else File.Move(temp, FilePath);
        }
        public static string CsvCell(string text)
        {
            text = text ?? "";
            string trimmed = text.TrimStart();
            if (trimmed.Length > 0 && "=+-@".IndexOf(trimmed[0]) >= 0) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        public static void Export(string path, IEnumerable<MatchRecord> records)
        {
            using (var file = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                file.WriteLine("Date;Match ID;Player;Opened;Broken;Total;Estimates;Status;Replay");
                foreach (var m in records) file.WriteLine(String.Join(";", new[] { CsvCell(m.DateText), CsvCell(m.match_id), CsvCell(m.player),
                    m.opened.ToString(CultureInfo.InvariantCulture), m.broken.ToString(CultureInfo.InvariantCulture), m.total.ToString(CultureInfo.InvariantCulture),
                    m.estimated.ToString(CultureInfo.InvariantCulture), m.status, CsvCell(m.file_name) }));
            }
        }
    }

    public static class Palette
    {
        public static readonly Color Background = Color.FromArgb(244, 247, 244);
        public static readonly Color Ink = Color.FromArgb(28, 48, 40);
        public static readonly Color Muted = Color.FromArgb(104, 120, 111);
        public static readonly Color Accent = Color.FromArgb(23, 105, 80);
        public static readonly Color Line = Color.FromArgb(224, 232, 225);
        public static Font Font(float size, FontStyle style = FontStyle.Regular) { return new Font("Segoe UI", size, style); }
    }

    public sealed class Surface : Panel
    {
        public Color Fill = Color.White;
        public Surface() { DoubleBuffered = true; BackColor = Color.Transparent; Padding = new Padding(18); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = new GraphicsPath())
            {
                int d = 20;
                p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
                using (Brush b = new SolidBrush(Fill)) e.Graphics.FillPath(b, p);
                using (Pen pen = new Pen(Palette.Line)) e.Graphics.DrawPath(pen, p);
            }
            base.OnPaint(e);
        }
    }

    public sealed class MainForm : Form
    {
        private readonly StatisticsStore store;
        private readonly string analyzer;
        private readonly DataGridView table = new DataGridView();
        private readonly ComboBox recorderFilter = new ComboBox();
        private readonly Label[] values = new Label[3];
        private readonly Label message = new Label(), empty = new Label(), footnote = new Label(), projectionText = new Label();
        private readonly NumericUpDown matches = new NumericUpDown();
        private readonly Button addFiles, addFolder, export, remove, cancel, clear;
        private readonly ProgressBar progress = new ProgressBar();
        private CancellationTokenSource cancellation;
        private bool busy, rebuilding;
        private string recorderKey;
        private ChestProjection projection = new ChestProjection(0, 0);
        internal bool IsImporting { get { return busy; } }
        internal int RecordCount { get { return store.Data.matches.Count; } }
        internal string DisplayedTotal { get { return values[0].Text; } }
        internal string DisplayedAverage { get { return values[2].Text; } }
        internal string ProjectionText { get { return projectionText.Text; } }
        internal string LastImportSummary { get { return message.Text; } }
        internal void SetMatches(int count) { matches.Value = count; }
        private sealed class Choice
        {
            public string Key, Name;
            public Choice(string key, string name) { Key = key; Name = name; }
            public override string ToString() { return Name; }
        }
        private static string OwnerKey(MatchRecord m) { return String.IsNullOrEmpty(m.player_id) ? "name:" + m.player : "id:" + m.player_id; }

        public MainForm(string statisticsPath, string analyzerPath)
        {
            store = new StatisticsStore(statisticsPath); analyzer = analyzerPath;
            Text = "Chest Counter · Fortnite"; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1120, 800); MinimumSize = new Size(940, 700);
            BackColor = Palette.Background; ForeColor = Palette.Ink; Font = Palette.Font(10); AutoScaleMode = AutoScaleMode.Dpi; KeyPreview = true;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 20, 28, 18), ColumnCount = 1, RowCount = 7, BackColor = Palette.Background };
            foreach (int height in new[] { 76, 58, 106, 108, 58 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); Controls.Add(root);
            var header = new Panel { Dock = DockStyle.Fill };
            header.Controls.Add(LabelAt("Your chest statistics", 0, 0, 680, 40, 25, FontStyle.Bold, Palette.Ink));
            header.Controls.Add(LabelAt("FORTNITE  /  PERSONAL REPLAY HISTORY", 2, 45, 650, 22, 9, FontStyle.Bold, Palette.Muted));
            var local = LabelAt("●  Stored on this PC", 0, 12, 220, 31, 10, FontStyle.Regular, Palette.Accent); local.TextAlign = ContentAlignment.MiddleRight;
            header.Controls.Add(local); header.Resize += delegate { local.Left = header.Width - local.Width; }; root.Controls.Add(header, 0, 0);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
            addFiles = MakeButton("＋  Add replays", true, 190); addFolder = MakeButton("Choose folder", false, 168); actions.Controls.Add(addFiles); actions.Controls.Add(addFolder);
            recorderFilter.DropDownStyle = ComboBoxStyle.DropDownList; recorderFilter.Width = 220; recorderFilter.Margin = new Padding(12, 8, 0, 0); actions.Controls.Add(recorderFilter); root.Controls.Add(actions, 0, 1);
            var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 5, 0, 6) };
            string[] captions = { "YOUR CHESTS", "MATCHES SAVED", "CHESTS PER MATCH" };
            for (int i = 0; i < 3; i++)
            {
                cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
                var card = new Surface { Dock = DockStyle.Fill, Margin = new Padding(i == 0 ? 0 : 6, 0, i == 2 ? 0 : 6, 0), Fill = i == 0 ? Color.FromArgb(229, 241, 231) : Color.White };
                var title = LabelAt(captions[i], 18, 12, 275, 23, 9, FontStyle.Bold, Palette.Muted);
                var number = LabelAt("0", 15, 34, 275, 57, 29, FontStyle.Bold, i == 0 ? Palette.Accent : Palette.Ink); values[i] = number;
                card.Controls.Add(title); card.Controls.Add(number);
                card.Resize += delegate { title.Width = Math.Max(80, card.Width - 36); number.Width = Math.Max(80, card.Width - 32); };
                cards.Controls.Add(card, i, 0);
            }
            root.Controls.Add(cards, 0, 2);
            var forecast = new Surface { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
            forecast.Controls.Add(LabelAt("Matches", 18, 13, 160, 23, 10, FontStyle.Bold, Palette.Ink));
            matches.Name = "matches"; matches.SetBounds(18, 44, 128, 31); matches.Minimum = 0; matches.Maximum = 1000000; matches.Value = 10; matches.ThousandsSeparator = true; matches.Font = Palette.Font(12);
            projectionText.SetBounds(175, 11, 800, 56); projectionText.Font = Palette.Font(14, FontStyle.Bold); projectionText.ForeColor = Palette.Accent;
            var basis = LabelAt("Enter your total match count. Based on your recorded average; loot estimates excluded.", 175, 71, 800, 22, 9, FontStyle.Regular, Palette.Muted);
            forecast.Controls.Add(matches); forecast.Controls.Add(projectionText); forecast.Controls.Add(basis);
            forecast.Resize += delegate { projectionText.Width = basis.Width = Math.Max(120, forecast.Width - 195); }; matches.ValueChanged += delegate { UpdateProjection(); }; root.Controls.Add(forecast, 0, 3);
            var historyHead = new Panel { Dock = DockStyle.Fill };
            historyHead.Controls.Add(LabelAt("Match history", 0, 17, 230, 30, 15, FontStyle.Bold, Palette.Ink));
            var tools = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 275, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0), WrapContents = false };
            export = MakeButton("Export CSV", false, 132); remove = MakeButton("Remove", false, 113); export.Height = remove.Height = 35; tools.Controls.Add(export); tools.Controls.Add(remove); historyHead.Controls.Add(tools); root.Controls.Add(historyHead, 0, 4);
            var tableSurface = new Surface { Dock = DockStyle.Fill, Padding = new Padding(12), Margin = new Padding(0) }; ConfigureTable(); tableSurface.Controls.Add(table);
            empty.Text = "Add your first replay\n\nYour match history, average and projection will appear here.";
            empty.TextAlign = ContentAlignment.MiddleCenter; empty.Font = Palette.Font(12); empty.ForeColor = Palette.Muted; empty.BackColor = Color.White; empty.Dock = DockStyle.Fill; tableSurface.Controls.Add(empty); root.Controls.Add(tableSurface, 0, 5);
            var bottom = new Panel { Dock = DockStyle.Fill };
            message.SetBounds(0, 8, 850, 23); message.ForeColor = Palette.Accent; message.AutoEllipsis = true;
            footnote.SetBounds(0, 34, 1000, 22); footnote.Font = Palette.Font(8.5f); footnote.ForeColor = Palette.Muted; footnote.AutoEllipsis = true;
            progress.SetBounds(0, 36, 850, 7); progress.Visible = false;
            cancel = MakeButton("Stop", false, 110); cancel.Height = 31; cancel.Top = 6; cancel.Visible = false;
            clear = MakeButton("Clear all data", false, 145); clear.Height = 31; clear.Top = 6; clear.ForeColor = Color.FromArgb(163, 49, 39);
            bottom.Resize += delegate { cancel.Left = bottom.Width - cancel.Width; clear.Left = bottom.Width - clear.Width; message.Width = bottom.Width - 160; progress.Width = bottom.Width - 160; footnote.Width = bottom.Width; };
            bottom.Controls.Add(message); bottom.Controls.Add(footnote); bottom.Controls.Add(progress); bottom.Controls.Add(cancel); bottom.Controls.Add(clear); root.Controls.Add(bottom, 0, 6);
            addFiles.Click += delegate { using (var d = new OpenFileDialog { Filter = "Fortnite replays (*.replay)|*.replay", Multiselect = true, InitialDirectory = DefaultReplayDirectory() }) if (d.ShowDialog(this) == DialogResult.OK) ImportPaths(d.FileNames); };
            addFolder.Click += delegate { using (var d = new FolderBrowserDialog { Description = "Choose a Fortnite replay folder", SelectedPath = DefaultReplayDirectory() }) if (d.ShowDialog(this) == DialogResult.OK) ImportPaths(new[] { d.SelectedPath }); };
            cancel.Click += delegate { if (cancellation != null) cancellation.Cancel(); }; clear.Click += ClearAllData; export.Click += ExportCsv; remove.Click += RemoveSelected;
            recorderFilter.SelectedIndexChanged += delegate { if (rebuilding) return; var c = recorderFilter.SelectedItem as Choice; if (c != null) { recorderKey = c.Key; RefreshRecords(false); } };
            table.CellContentClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0 && e.ColumnIndex == 5) { table.CurrentCell = table.Rows[e.RowIndex].Cells[0]; ShowDetails(); } };
            table.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0 && e.ColumnIndex != 5) ShowDetails(); };
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Control && e.KeyCode == Keys.O && !busy) { e.SuppressKeyPress = true; addFiles.PerformClick(); } };
            FormClosing += delegate { if (cancellation != null) cancellation.Cancel(); };
            EnableDrop(this); EnableDrop(table); EnableDrop(empty); RefreshRecords(true); message.Text = String.IsNullOrEmpty(store.RecoveryNotice) ? "Ready to analyze" : store.RecoveryNotice;
        }
        private static Label LabelAt(string text, int x, int y, int w, int h, float size, FontStyle style, Color color)
        { return new Label { Text = text, Bounds = new Rectangle(x, y, w, h), Font = Palette.Font(size, style), ForeColor = color, BackColor = Color.Transparent, AutoEllipsis = true }; }
        private static Button MakeButton(string text, bool primary, int width)
        {
            var b = new Button { Text = text, Width = width, Height = 42, FlatStyle = FlatStyle.Flat, BackColor = primary ? Palette.Accent : Color.White, ForeColor = primary ? Color.White : Palette.Ink, Font = Palette.Font(10, FontStyle.Bold), Cursor = Cursors.Hand, Margin = new Padding(0, 0, 10, 0), UseVisualStyleBackColor = false };
            b.FlatAppearance.BorderColor = primary ? Palette.Accent : Palette.Line; return b;
        }
        private static string DefaultReplayDirectory()
        { string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos"); return Directory.Exists(p) ? p : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
        private void ConfigureTable()
        {
            table.Dock = DockStyle.Fill; table.BackgroundColor = Color.White; table.BorderStyle = BorderStyle.None;
            table.AllowUserToAddRows = table.AllowUserToDeleteRows = false; table.ReadOnly = true; table.MultiSelect = false; table.RowHeadersVisible = false; table.SelectionMode = DataGridViewSelectionMode.FullRowSelect; table.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            table.EnableHeadersVisualStyles = false; table.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None; table.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; table.GridColor = Palette.Line; table.RowTemplate.Height = 48; table.ColumnHeadersHeight = 38;
            table.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(247, 249, 247), ForeColor = Palette.Muted, Font = Palette.Font(9, FontStyle.Bold), Padding = new Padding(8, 0, 0, 0) };
            table.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = Palette.Ink, SelectionBackColor = Color.FromArgb(229, 241, 231), SelectionForeColor = Palette.Ink, Font = Palette.Font(10), Padding = new Padding(8, 0, 0, 0), WrapMode = DataGridViewTriState.True };
            string[] names = { "Match", "Opened", "Broken", "Total", "Status" }; float[] widths = { 36, 14, 14, 14, 18 };
            for (int i = 0; i < names.Length; i++) { int n = table.Columns.Add("c" + i, names[i]); table.Columns[n].FillWeight = widths[i]; table.Columns[n].SortMode = DataGridViewColumnSortMode.NotSortable; }
            table.Columns.Add(new DataGridViewLinkColumn { Name = "details", HeaderText = "Details", Text = "Open", UseColumnTextForLinkValue = true, FillWeight = 12, LinkColor = Palette.Accent, ActiveLinkColor = Palette.Accent, VisitedLinkColor = Palette.Accent, TrackVisitedState = false });
            for (int i = 1; i <= 3; i++) table.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        }
        private void EnableDrop(Control control)
        {
            try { control.AllowDrop = true; } catch (InvalidOperationException) { control.AllowDrop = false; return; }
            control.DragEnter += delegate(object sender, DragEventArgs e) { if (!busy && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            control.DragDrop += delegate(object sender, DragEventArgs e) { if (!busy) ImportPaths((string[])e.Data.GetData(DataFormats.FileDrop)); };
        }
        private List<MatchRecord> Filtered() { return store.Data.matches.Where(m => OwnerKey(m) == recorderKey).OrderByDescending(m => m.timestamp_ms ?? 0).ToList(); }
        private void RefreshRecords(bool rebuildFilter)
        {
            if (rebuildFilter)
            {
                rebuilding = true;
                try
                {
                    var owners = store.Data.matches.OrderByDescending(m => m.timestamp_ms ?? 0).GroupBy(OwnerKey).Select(g => new Choice(g.Key, g.First().player)).ToList();
                    if (!owners.Any(c => c.Key == recorderKey)) recorderKey = owners.Count > 0 ? owners[0].Key : null;
                    recorderFilter.Items.Clear(); foreach (var c in owners) recorderFilter.Items.Add(c);
                    recorderFilter.SelectedItem = recorderFilter.Items.Cast<Choice>().FirstOrDefault(c => c.Key == recorderKey); recorderFilter.Visible = owners.Count > 1;
                }
                finally { rebuilding = false; }
            }
            var rows = Filtered(); projection = new ChestProjection(rows.Count, rows.Sum(m => (long)m.total));
            values[0].Text = projection.Chests.ToString("N0", CultureInfo.InvariantCulture); values[1].Text = projection.Matches.ToString("N0", CultureInfo.InvariantCulture);
            values[2].Text = projection.Average.HasValue ? projection.Average.Value.ToString("N1", CultureInfo.InvariantCulture) : "—";
            table.Rows.Clear();
            foreach (var m in rows)
            {
                string id = m.match_id.Length > 12 ? m.match_id.Substring(0, 12) : m.match_id;
                int i = table.Rows.Add(m.DateText + "\n#" + id, m.opened, m.broken, m.total, m.status == "partial" ? "Partial" : "Parsed", "Open"); table.Rows[i].Tag = m;
            }
            table.ClearSelection(); empty.Visible = rows.Count == 0; table.Visible = !empty.Visible; export.Enabled = remove.Enabled = !busy && rows.Count > 0;
            footnote.Text = "Recording player only · opened + broken · possible loot estimates excluded · missing replay data can lower the average"; UpdateProjection();
        }
        private void UpdateProjection()
        {
            matches.Enabled = !busy && projection.Matches > 0;
            int count = (int)matches.Value; var total = projection.ProjectedTotal(count);
            projectionText.Text = total.HasValue ? "≈ " + total.Value.ToString("N0", CultureInfo.InvariantCulture) + " total chests\nfor " + count.ToString("N0", CultureInfo.InvariantCulture) + " matches"
                : "Import at least one completed replay\nto calculate a projection.";
        }
        private void SetBusy(bool value)
        {
            busy = value; addFiles.Enabled = addFolder.Enabled = recorderFilter.Enabled = clear.Enabled = !value; export.Enabled = remove.Enabled = !value && table.Rows.Count > 0;
            progress.Visible = cancel.Visible = value; clear.Visible = footnote.Visible = !value; if (value) progress.Value = 0; UpdateProjection();
        }
        private static string HashFile(string path)
        { using (var hash = SHA256.Create()) using (var file = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant(); }
        private static string QuoteArgument(string value)
        {
            // Windows CommandLineToArgvW quoting. Never use a shell for replay filenames.
            var text = new StringBuilder("\""); int backslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') text.Append('\\', backslashes * 2 + 1).Append(c);
                else text.Append('\\', backslashes).Append(c);
                backslashes = 0;
            }
            return text.Append('\\', backslashes * 2).Append('"').ToString();
        }
        private MatchRecord Analyze(string path, CancellationToken token)
        {
            var info = new ProcessStartInfo(analyzer, QuoteArgument(path)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(analyzer) };
            using (var process = new Process { StartInfo = info })
            {
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    int percent;
                    if (e.Data != null && e.Data.StartsWith("PROGRESS ") && Int32.TryParse(e.Data.Substring(9), out percent) && !IsDisposed && IsHandleCreated)
                        try { BeginInvoke((Action)delegate { progress.Value = Math.Max(0, Math.Min(100, percent)); }); } catch (InvalidOperationException) { }
                };
                process.Start(); process.BeginErrorReadLine(); Task<string> output = process.StandardOutput.ReadToEndAsync(); var timer = Stopwatch.StartNew();
                while (!process.WaitForExit(100))
                {
                    if (token.IsCancellationRequested || timer.Elapsed.TotalSeconds > 120)
                    {
                        try { process.Kill(); process.WaitForExit(); } catch (InvalidOperationException) { }
                        token.ThrowIfCancellationRequested(); throw new TimeoutException("Analysis took longer than two minutes.");
                    }
                }
                process.WaitForExit(); token.ThrowIfCancellationRequested();
                string text = output.GetAwaiter().GetResult();
                if (text.Length > 16 * 1024 * 1024) throw new InvalidDataException("The analysis result is too large.");
                var json = StatisticsStore.Json(); var error = json.Deserialize<Dictionary<string, object>>(text);
                if (error.ContainsKey("error")) throw new InvalidDataException(TranslateError(Convert.ToString(error["error"])));
                if (process.ExitCode != 0) throw new InvalidDataException("The parser exited with an error.");
                MatchRecord record = json.Deserialize<MatchRecord>(text); record.Validate(); return record;
            }
        }
        private static string TranslateError(string text)
        {
            if (text.Contains("encryption key") || text.Contains("incomplete:")) return "The match is still being recorded. Try again after returning to the lobby.";
            if (text.StartsWith("owner:")) return "The recording player could not be identified.";
            if (text.StartsWith("size:")) return "The file must be non-empty and no larger than 256 MiB.";
            if (text.Contains("not a replay") || text.StartsWith("corrupt:")) return "The file is damaged or is not a Fortnite replay.";
            if (text.StartsWith("timeout:")) return "The analysis time limit was exceeded.";
            return "Unable to read the replay. Its format may be incompatible.\n" + text;
        }
        public async void ImportPaths(string[] paths)
        {
            if (busy || paths == null) return;
            if (!File.Exists(analyzer)) { MessageBox.Show(this, "bin\\replay-analyzer.exe was not found. Keep the application files together.", "Parser not found", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            List<string> files;
            try { files = paths.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.replay", SearchOption.TopDirectoryOnly) : new[] { p }).Where(p => String.Equals(Path.GetExtension(p), ".replay", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(File.GetLastWriteTimeUtc).ToList(); }
            catch (Exception e) { MessageBox.Show(this, e.Message, "Unable to open folder"); return; }
            if (files.Count == 0) { message.Text = "No .replay files were found in the selected location"; return; }
            int added = 0, updated = 0, skipped = 0; var errors = new List<string>();
            cancellation = new CancellationTokenSource(); SetBusy(true);
            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    cancellation.Token.ThrowIfCancellationRequested(); string path = files[i];
                    message.Text = String.Format("Analyzing {0} / {1}  ·  {2}", i + 1, files.Count, Path.GetFileName(path)); progress.Value = 0;
                    try
                    {
                        var fileInfo = new FileInfo(path);
                        if (fileInfo.Length == 0 || fileInfo.Length > 256L * 1024 * 1024) throw new InvalidDataException("The file is empty or larger than 256 MiB.");
                        string hash = await Task.Run(() => HashFile(path));
                        if (store.Data.matches.Any(m => m.file_hash == hash)) { skipped++; continue; }
                        MatchRecord record = await Task.Run(() => Analyze(path, cancellation.Token));
                        string afterHash = await Task.Run(() => HashFile(path));
                        if (hash != afterHash) throw new IOException("The file changed during analysis. Try again after the match ends.");
                        record.file_hash = hash; record.file_name = Path.GetFileName(path); record.imported_utc = DateTime.UtcNow.ToString("o");
                        if (store.Upsert(record)) added++; else updated++;
                        if (!IsDisposed) RefreshRecords(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { errors.Add(Path.GetFileName(path) + ": " + e.Message); }
                }
            }
            catch (OperationCanceledException) { errors.Add("Import stopped. Matches already added have been saved."); }
            finally
            {
                cancellation.Dispose(); cancellation = null;
                if (!IsDisposed)
                {
                    SetBusy(false); RefreshRecords(true);
                    message.Text = String.Format("Added: {0}  ·  updated: {1}  ·  duplicates skipped: {2}{3}", added, updated, skipped, errors.Count > 0 ? "  ·  see messages" : "");
                    message.Refresh();
                    if (errors.Count > 0) MessageBox.Show(this, String.Join("\n\n", errors.Take(12)) + (errors.Count > 12 ? "\n\nAdditional errors: " + (errors.Count - 12) : ""), "Import results", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }
        private MatchRecord Selected() { return table.SelectedRows.Count == 0 ? null : table.SelectedRows[0].Tag as MatchRecord; }
        private void RemoveSelected(object sender, EventArgs e)
        {
            var record = Selected(); if (record == null) { message.Text = "Select a match in the table first"; return; }
            if (MessageBox.Show(this, "Remove this match from statistics? The replay file will be kept.", "Remove match", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { store.Remove(record); RefreshRecords(true); message.Text = "Match removed from statistics"; } catch (Exception error) { MessageBox.Show(this, error.Message, "Unable to save"); }
        }
        internal static Form CreateClearConfirmation()
        {
            var dialog = new Form { Text = "Clear all data", ClientSize = new Size(465, 295), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, BackColor = Palette.Background, Font = Palette.Font(10), AutoScaleMode = AutoScaleMode.Dpi };
            dialog.Controls.Add(LabelAt("Clear all data?", 24, 19, 410, 32, 17, FontStyle.Bold, Palette.Ink));
            dialog.Controls.Add(LabelAt("Delete all saved matches, personal statistics and application backups.", 24, 64, 410, 48, 10, FontStyle.Regular, Palette.Ink));
            dialog.Controls.Add(LabelAt("Your original Fortnite replay files are kept.", 24, 119, 410, 22, 9, FontStyle.Regular, Palette.Muted));
            dialog.Controls.Add(LabelAt("Type DELETE to confirm", 24, 157, 410, 23, 10, FontStyle.Bold, Palette.Ink));
            var input = new TextBox { Name = "deleteConfirmation", Bounds = new Rectangle(24, 185, 410, 28), Font = Palette.Font(11) };
            var cancelButton = MakeButton("Cancel", false, 110); cancelButton.SetBounds(180, 238, 110, 34); cancelButton.DialogResult = DialogResult.Cancel;
            var confirm = MakeButton("Clear data", true, 130); confirm.Name = "confirmDelete"; confirm.SetBounds(304, 238, 130, 34); confirm.BackColor = Color.FromArgb(171, 53, 43); confirm.FlatAppearance.BorderColor = confirm.BackColor; confirm.Enabled = false; confirm.DialogResult = DialogResult.OK;
            input.TextChanged += delegate { confirm.Enabled = input.Text == "DELETE"; };
            dialog.Controls.Add(input); dialog.Controls.Add(cancelButton); dialog.Controls.Add(confirm); dialog.AcceptButton = confirm; dialog.CancelButton = cancelButton;
            dialog.Shown += delegate { input.Focus(); }; return dialog;
        }
        private void ClearAllData(object sender, EventArgs e)
        {
            if (busy) return;
            using (var dialog = CreateClearConfirmation()) if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { store.Clear(); message.Text = "All saved statistics and application backups cleared"; }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Clear data"); }
            finally { recorderKey = null; RefreshRecords(true); }
        }
        internal void ExportCurrent(string path) { StatisticsStore.Export(path, Filtered()); }

        private void ExportCsv(object sender, EventArgs e)
        {
            using (var dialog = new SaveFileDialog { Filter = "CSV file (*.csv)|*.csv", FileName = "fortnite-chests-" + DateTime.Today.ToString("yyyy-MM-dd") + ".csv" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { ExportCurrent(dialog.FileName); message.Text = "Statistics exported"; } catch (Exception error) { MessageBox.Show(this, error.Message, "Export failed"); }
            }
        }
        private void ShowDetails()
        {
            var record = Selected(); if (record == null) return;
            using (var dialog = CreateMatchDetails(record)) dialog.ShowDialog(this);
        }
        internal Form CreateMatchDetails(MatchRecord record)
        {
            var dialog = new Form { Text = "Match details", ClientSize = new Size(780, 550), MinimumSize = new Size(620, 450), StartPosition = FormStartPosition.CenterParent, BackColor = Palette.Background, Font = Palette.Font(10), Padding = new Padding(22), AutoScaleMode = AutoScaleMode.Dpi };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            var metadata = new TextBox { Name = "matchMetadata", Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, BackColor = Palette.Background, ForeColor = Palette.Ink, Font = Palette.Font(10),
                Text = record.DateText + "  ·  " + record.branch + Environment.NewLine + "Player: " + record.player + Environment.NewLine + "Match ID: " + record.match_id + Environment.NewLine + "Replay: " + record.file_name };
            layout.Controls.Add(metadata, 0, 0);
            layout.Controls.Add(new Label { Name = "matchCounts", Dock = DockStyle.Fill, Font = Palette.Font(11, FontStyle.Bold), ForeColor = Palette.Accent,
                Text = "Opened: " + record.opened + "    Broken: " + record.broken + "    Total: " + record.total + "    Estimates: " + record.estimated }, 0, 1);
            var eventText = new TextBox { Name = "personalEvents", Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = Palette.Ink };
            var lines = new List<string> { "YOUR EVENTS SINCE RECORDING START", "" }; AddEvents(lines, record.player, record.events);
            lines.Add("Packet errors: " + record.failed_packets + "; incomplete chunks: " + record.broken_chunks + "; parsed: " + record.packet_health.ToString("P2"));
            eventText.Text = String.Join(Environment.NewLine, lines); layout.Controls.Add(eventText, 0, 2);
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Palette.Muted, Font = Palette.Font(9), Padding = new Padding(0, 10, 0, 0), Text = "Only your recorded events are counted. Possible loot estimates are excluded from totals and projections." }, 0, 3);
            dialog.Controls.Add(layout); dialog.Shown += delegate { eventText.Focus(); eventText.Select(0, 0); metadata.Select(0, 0); }; return dialog;
        }
        private static void AddEvents(List<string> lines, string player, List<ChestEvent> events)
        {
            lines.Add(player.ToUpperInvariant());
            foreach (var ev in events.OrderBy(e => e.seconds)) lines.Add(TimeSpan.FromSeconds(Math.Max(0, ev.seconds)).ToString(@"mm\:ss") + "   " + (ev.action == "opened" ? "Opened" : ev.action == "broken" ? "Broken" : "Possible chest") + "   ·   " + ev.kind);
            if (events.Count == 0) lines.Add("No chest events recorded."); lines.Add("");
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string root = AppDomain.CurrentDomain.BaseDirectory;
            string mutexName;
            using (var hash = SHA256.Create()) mutexName = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant()))).Replace("-", "").Substring(0, 20);
            bool created;
            using (var instance = new Mutex(true, "Local\\FortniteChestCounter-" + mutexName, out created))
            {
                if (!created) { MessageBox.Show("The application is already running.", "Chest statistics"); return; }
                try
                {
                    var form = new MainForm(Path.Combine(root, "data", "statistics.json"), Path.Combine(root, "bin", "replay-analyzer.exe"));
                    if (args.Length > 0) form.Shown += delegate { form.ImportPaths(args); };
                    Application.Run(form);
                }
                catch (Exception e) { MessageBox.Show(e.Message, "Unable to start the application", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
    }
}
