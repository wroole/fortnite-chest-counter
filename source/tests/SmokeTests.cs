using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using ChestCounter;
public static class SmokeTests
{
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private static MatchRecord Clone(MatchRecord record) { return StatisticsStore.Json().Deserialize<MatchRecord>(StatisticsStore.Json().Serialize(record)); }
    private static string Hash(string path) { using (var hash = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    private static void Wait(MainForm form)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(35);
        while (form.IsImporting && DateTime.UtcNow < limit) { Application.DoEvents(); Thread.Sleep(10); }
        Check(!form.IsImporting, "import finishes without blocking UI"); Application.DoEvents(); form.Refresh(); Application.DoEvents();
    }
    [STAThread] public static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        int result = 1;
        using (var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(20, 20) })
        { host.Shown += delegate { try { result = Run(args); } finally { host.Close(); } }; Application.Run(host); }
        return result;
    }
    private static int Run(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            var p = new ChestProjection(10, 200);
            Check(p.Average == 20m && p.ProjectedTotal(15) == 300, "200 chests / 10 saved matches projects 300 chests for 15 total matches");
            Check(p.ProjectedTotal(10) == 200 && p.ProjectedTotal(5) == 100, "entered match count is used directly without adding saved matches or chests");
            Check(p.ProjectedTotal(0) == 0, "zero total matches projects zero chests");
            Check(!new ChestProjection(0, 0).Average.HasValue && !new ChestProjection(0, 0).ProjectedTotal(5).HasValue, "empty history has no invented average or forecast");
            Check(new ChestProjection(3, 100).ProjectedTotal(3) == 100, "forecast uses full precision, not the rounded displayed average");
            Check(new ChestProjection(2, 1).ProjectedTotal(3) == 2, "fractional forecast rounds only the final chest count");
            Check(new ChestProjection(1, 20000).ProjectedTotal(1000000) == 20000000000L, "large projections do not overflow a 32-bit integer");
            bool invalid = false; try { p.ProjectedTotal(-1); } catch (ArgumentOutOfRangeException) { invalid = true; } Check(invalid, "negative total matches are rejected");
            string raw = File.ReadAllText(args[1]); Check(!raw.Contains("teammate") && !raw.Contains("team_counts"), "parser output contains only the recording player");
            var real = StatisticsStore.Json().Deserialize<MatchRecord>(raw); real.Validate(); Check(real.schema_version == 3, "personal-only parser schema is current");
            real.file_name = "match.replay"; real.file_hash = Hash(args[3]); real.imported_utc = DateTime.UtcNow.ToString("o");
            string path = Path.Combine(root, "storage", "statistics.json"); var store = new StatisticsStore(path);
            Check(store.Upsert(real), "first import adds a match"); var copy = Clone(real); copy.file_hash = "changed-copy";
            Check(!store.Upsert(copy) && store.Data.matches.Count == 1, "same match identity does not duplicate after file changes");
            Check(new StatisticsStore(path).Data.matches.Count == 1, "saved history survives reopening");
            var other = Clone(real); other.player_id = "another-recorder"; other.player = "Another recorder";
            Check(store.Upsert(other) && store.Data.matches.Count == 2, "different recording accounts remain separate"); store.Remove(other);
            Check(new StatisticsStore(path).Data.matches.Count == 1, "removal persists");
            File.WriteAllText(path, "broken-json"); var recovered = new StatisticsStore(path);
            Check(recovered.RecoveryNotice.Length > 0 && recovered.Data.matches.Count == 2, "damaged statistics recover from the prior backup");
            Check(StatisticsStore.CsvCell("=1+1").StartsWith("\"'"), "CSV formula injection remains escaped");
            var bad = Clone(real); bad.total++; invalid = false; try { bad.Validate(); } catch (InvalidDataException) { invalid = true; } Check(invalid, "inconsistent chest totals are rejected");
            string legacyPath = Path.Combine(root, "legacy", "statistics.json"); Directory.CreateDirectory(Path.GetDirectoryName(legacyPath));
            var legacy = StatisticsStore.Json().Deserialize<Dictionary<string, object>>(StatisticsStore.Json().Serialize(real)); legacy["schema_version"] = 2;
            legacy["teammates"] = new[] { new { name = "Legacy teammate", opened = 900 } }; legacy["teammates_total"] = 900;
            File.WriteAllText(legacyPath, StatisticsStore.Json().Serialize(new { schema_version = 1, matches = new[] { legacy } }));
            byte[] legacyBytes = File.ReadAllBytes(legacyPath);
            using (var form = NewForm(legacyPath, args[2]))
            {
                form.Show(); Application.DoEvents(); Check(form.RecordCount == 1 && form.DisplayedTotal == real.total.ToString("N0", CultureInfo.InvariantCulture), "old history loads while ignoring legacy team counts");
                form.ImportPaths(new[] { args[3] }); Wait(form);
                Check(form.LastImportSummary.Contains("duplicates skipped: 1") && legacyBytes.SequenceEqual(File.ReadAllBytes(legacyPath)), "old imported replays are skipped without rewriting history"); form.Close();
            }
            string guiPath = Path.Combine(root, "gui", "statistics.json");
            using (var form = NewForm(guiPath, args[2]))
            {
                form.Show(); Application.DoEvents(); Check(form.DisplayedAverage == "—" && form.ProjectionText.Contains("Import at least one"), "empty UI explains that a forecast needs history");
                form.ImportPaths(new[] { args[3] }); Wait(form); Check(form.RecordCount == 1, "real replay imports through the UI");
                Check(form.DisplayedAverage == real.total.ToString("N1", CultureInfo.InvariantCulture), "average includes opened plus broken chests and excludes loot estimates");
                form.SetMatches(5);
                Check(form.ProjectionText.Contains((real.total * 5L).ToString("N0", CultureInfo.InvariantCulture) + " total chests") && form.ProjectionText.Contains("for 5 matches") && !form.ProjectionText.Contains("additional"), "UI forecast uses total matches without adding saved history");
                Check(form.Controls.Find("matches", true).Single() is NumericUpDown, "match count input is named matches");
                form.SetMatches(0); Check(form.ProjectionText.Contains("≈ 0 total chests\nfor 0 matches"), "UI handles zero total matches");
                string csv = Path.Combine(root, "personal.csv"); form.ExportCurrent(csv); string[] lines = File.ReadAllLines(csv);
                Check(lines.Length == 2 && !lines[0].ToLowerInvariant().Contains("team") && lines[1].Contains(StatisticsStore.CsvCell(real.player)), "CSV contains only the recorder's own row");
                using (var details = form.CreateMatchDetails(real)) Check(details.Controls.Find("matchMetadata", true).Single().Text.Contains(real.match_id) && details.Controls.Find("personalEvents", true).Length == 1, "match details show only personal events and identify the match");
                form.Close();
            }
            string scan = Path.Combine(root, "scan"); Directory.CreateDirectory(scan); File.Copy(args[3], Path.Combine(scan, "original.replay")); File.Copy(args[3], Path.Combine(scan, "renamed.replay"));
            byte[] before = File.ReadAllBytes(guiPath); DateTime modified = File.GetLastWriteTimeUtc(guiPath);
            using (var form = NewForm(guiPath, args[2]))
            {
                form.Show(); Application.DoEvents(); form.ImportPaths(new[] { scan }); Wait(form);
                Check(form.RecordCount == 1 && form.LastImportSummary.Contains("duplicates skipped: 2"), "folder rescan after reopening skips original and renamed copies"); form.Close();
            }
            Check(before.SequenceEqual(File.ReadAllBytes(guiPath)) && modified == File.GetLastWriteTimeUtc(guiPath), "duplicate folder scan does not rewrite statistics");
            using (var confirm = MainForm.CreateClearConfirmation())
            {
                var input = (TextBox)confirm.Controls.Find("deleteConfirmation", true).Single(); var button = (Button)confirm.Controls.Find("confirmDelete", true).Single();
                Check(!button.Enabled, "clear requires explicit confirmation"); input.Text = "delete"; Check(!button.Enabled, "lowercase confirmation is rejected"); input.Text = "DELETE"; Check(button.Enabled, "exact DELETE enables clear"); confirm.DialogResult = DialogResult.Cancel;
            }
            Check(before.SequenceEqual(File.ReadAllBytes(guiPath)), "canceling confirmation does not change data");
            string canary = Path.Combine(Path.GetDirectoryName(path), "keep.replay"); File.WriteAllText(canary, "preserve replay"); recovered.Clear();
            Check(new StatisticsStore(path).Data.matches.Count == 0 && !File.Exists(path + ".bak") && Directory.GetFiles(Path.GetDirectoryName(path), "statistics.json.damaged-*").Length == 0, "clear persists empty history and removes managed backup copies");
            Check(File.ReadAllText(canary) == "preserve replay", "clear keeps replay files");
            Console.WriteLine("ALL CHECKS PASSED"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static MainForm NewForm(string path, string analyzer)
    { return new MainForm(path, analyzer) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false }; }
}
