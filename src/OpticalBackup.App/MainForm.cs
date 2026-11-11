using System.Diagnostics;
using System.Text;
using OpticalBackup.Core;
using OpticalBackup.Core.Backup;
using OpticalBackup.Core.Catalog;

namespace OpticalBackup.App;

public sealed class MainForm : Form
{
    private const string AppTitle = "Optical Backup Assistant";
    private const string CustomDiscLabel = "Custom size...";

    private readonly AppSettings _settings = AppSettings.Load();
    private BackupCatalog _catalog = new();
    private bool _catalogUsable = true;
    private BackupPlan? _plan;
    private CancellationTokenSource? _cts;

    // Back up tab
    private readonly ComboBox _job = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _source = new();
    private readonly ComboBox _discType = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _customMb = new() { Minimum = 10, Maximum = 200_000, Increment = 100, ThousandsSeparator = true };
    private readonly NumericUpDown _recovery = new() { Minimum = 0, Maximum = 30, DecimalPlaces = 0 };
    private readonly TextBox _output = new();
    private readonly ComboBox _outputMode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _rehash = new() { Text = "Re-read every file, even if its size and date are unchanged (slow, most thorough)", AutoSize = true };
    private readonly Button _scan = new() { Text = "1. Scan and plan", AutoSize = true };
    private readonly Button _create = new() { Text = "2. Create discs", AutoSize = true, Enabled = false };
    private readonly Button _burn = new() { Text = "Burn selected image...", AutoSize = true, Enabled = false };
    private readonly Button _openOutput = new() { Text = "Open output folder", AutoSize = true };
    private readonly TextBox _backupLog = NewLog();
    private readonly ListBox _images = new() { IntegralHeight = false, Height = 90 };

    // Verify tab
    private readonly ComboBox _verifyLocation = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button _verify = new() { Text = "Verify disc", AutoSize = true };
    private readonly TextBox _verifyLog = NewLog();

    // Restore tab
    private readonly ComboBox _restoreJob = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _restoreDest = new();
    private readonly ComboBox _restoreLocation = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button _restore = new() { Text = "Restore from this disc", AutoSize = true };
    private readonly Button _restoreRefresh = new() { Text = "Refresh list", AutoSize = true };
    private readonly ListView _restoreDiscs = NewList("Disc label", "Run date", "Files needed", "Restored", "Status");
    private readonly TextBox _restoreLog = NewLog();

    // Catalog tab
    private readonly TextBox _catalogPath = new() { ReadOnly = true };
    private readonly ListView _catalogList = NewList("Job / disc", "Date", "Files", "Size", "Verified");
    private readonly Button _forgetRun = new() { Text = "Forget last run of selected job...", AutoSize = true };

    // Shared
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 1000 };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Text = "Ready." };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };

    public MainForm()
    {
        Text = AppTitle;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        ClientSize = new Size(900, 660);
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;

        _tabs.TabPages.Add(BuildBackupTab());
        _tabs.TabPages.Add(BuildVerifyTab());
        _tabs.TabPages.Add(BuildRestoreTab());
        _tabs.TabPages.Add(BuildCatalogTab());

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 2, RowCount = 2, AutoSize = true, Padding = new Padding(8, 4, 8, 8) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        bottom.Controls.Add(_status, 0, 0);
        bottom.SetColumnSpan(_status, 2);
        bottom.Controls.Add(_progress, 0, 1);
        bottom.Controls.Add(_cancel, 1, 1);

        Controls.Add(_tabs);
        Controls.Add(bottom);

        _cancel.Click += (_, _) => _cts?.Cancel();
        FormClosing += OnFormClosing;
        Load += (_, _) => LoadCatalog();
    }

    // ------------------------------------------------------------------ layout helpers

    private static TextBox NewLog() => new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 9f), WordWrap = true, BackColor = SystemColors.Window,
    };

    private static ListView NewList(params string[] columns)
    {
        var list = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false, MultiSelect = false };
        foreach (var c in columns) list.Columns.Add(c, 150);
        list.Columns[0].Width = 260;
        return list;
    }

    private static TableLayoutPanel NewGrid()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return grid;
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control control, Control? extra = null)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 6) }, 0, row);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        grid.Controls.Add(control, 1, row);
        if (extra != null) grid.Controls.Add(extra, 2, row);
        else grid.SetColumnSpan(control, 2);
    }

    private static void AddWide(TableLayoutPanel grid, Control control, bool fill = false)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        if (!fill && control.Dock == DockStyle.None) control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        grid.Controls.Add(control, 0, row);
        grid.SetColumnSpan(control, 3);
    }

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
        flow.Controls.AddRange(controls);
        return flow;
    }

    private static Label Note(string text) => new()
        { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 8), MaximumSize = new Size(840, 0) };

    private Button BrowseFolderButton(Control target, string description)
    {
        var button = new Button { Text = "Browse...", AutoSize = true };
        button.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
            if (Directory.Exists(target.Text)) dialog.SelectedPath = target.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
        };
        return button;
    }

    // ------------------------------------------------------------------ tabs

    private TabPage BuildBackupTab()
    {
        var page = new TabPage("Back up");
        var grid = NewGrid();

        foreach (var d in DiscTypes.All) _discType.Items.Add(d);
        _discType.Items.Add(CustomDiscLabel);
        _discType.SelectedItem = (object?)DiscTypes.ById(_settings.DiscTypeId) ?? CustomDiscLabel;
        _customMb.Value = Math.Clamp(_settings.CustomDiscMegabytes, (int)_customMb.Minimum, (int)_customMb.Maximum);
        _customMb.Width = 110;
        _recovery.Value = (decimal)Math.Clamp(_settings.RecoveryPercent, 0, 30);
        _recovery.Width = 60;
        _outputMode.Items.AddRange(new object[] { "ISO image per disc (burn with any burning program)", "Folder per disc (drag the contents into your own burning program)" });
        _outputMode.SelectedIndex = _settings.FolderOutput ? 1 : 0;
        _output.Text = _settings.OutputDirectory ?? "";
        var customLabel = new Label { Text = "MB", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };

        AddRow(grid, "Backup job:", _job);
        AddWide(grid, Note("A job is one folder you back up over time (for example \"Photos\"). Pick an existing job to add only what is new or changed since its last discs."));
        AddRow(grid, "Folder to back up:", _source, BrowseFolderButton(_source, "Folder to back up (local or network)"));
        AddRow(grid, "Disc type:", Flow(_discType, _customMb, customLabel));
        AddRow(grid, "Recovery data:", Flow(_recovery, new Label { Text = "% of each disc (lets a scratched disc be repaired; 0 = none)", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }));
        AddRow(grid, "Write output to:", _output, BrowseFolderButton(_output, "Folder for the disc images"));
        AddRow(grid, "Output as:", _outputMode);
        AddWide(grid, _rehash);
        AddWide(grid, Flow(_scan, _create));
        AddWide(grid, _backupLog, fill: true);
        AddWide(grid, new Label { Text = "Disc images created:", AutoSize = true, Margin = new Padding(3, 8, 3, 2) });
        _images.Dock = DockStyle.Fill;
        AddWide(grid, _images);
        AddWide(grid, Flow(_burn, _openOutput));
        _discType.Width = 240;

        void UpdateCustom() => _customMb.Visible = customLabel.Visible = Equals(_discType.SelectedItem, CustomDiscLabel);
        UpdateCustom();
        _discType.SelectedIndexChanged += (_, _) => { UpdateCustom(); InvalidatePlan(); };
        _customMb.ValueChanged += (_, _) => InvalidatePlan();
        _recovery.ValueChanged += (_, _) => InvalidatePlan();
        _source.TextChanged += (_, _) => InvalidatePlan();
        _rehash.CheckedChanged += (_, _) => InvalidatePlan();
        _job.TextChanged += (_, _) => InvalidatePlan();
        _job.SelectedIndexChanged += (_, _) =>
        {
            if (_catalog.FindJob(_job.Text)?.LastSourcePath is { } path) _source.Text = path;
        };
        _scan.Click += async (_, _) => await ScanAsync();
        _create.Click += async (_, _) => await CreateDiscsAsync();
        _images.SelectedIndexChanged += (_, _) => _burn.Enabled = _images.SelectedItem is string s && s.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
        _burn.Click += (_, _) => BurnSelected();
        _openOutput.Click += (_, _) => OpenFolder(_output.Text);

        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildVerifyTab()
    {
        var page = new TabPage("Verify disc");
        var grid = NewGrid();
        AddWide(grid, Note("After burning, put each disc back in the drive and verify it. Every file is read back and compared with its checksum. " +
                           "Discs that pass are marked as verified in the catalog. It is also worth re-verifying stored discs every few years."));
        AddRow(grid, "Disc drive or folder:", _verifyLocation, BrowseFolderButton(_verifyLocation, "Disc drive, mounted image or folder copy of a disc"));
        AddWide(grid, Flow(_verify));
        AddWide(grid, _verifyLog, fill: true);
        _verify.Click += async (_, _) => await VerifyAsync();
        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildRestoreTab()
    {
        var page = new TabPage("Restore");
        var grid = NewGrid();
        AddWide(grid, Note("Rebuilds a job's folder exactly as it was at its last backup. Insert each disc in the list (any order), then press Restore. " +
                           "On a new computer, first load the catalog from your most recent disc on the Catalog tab."));
        AddRow(grid, "Backup job:", _restoreJob);
        AddRow(grid, "Restore into folder:", _restoreDest, BrowseFolderButton(_restoreDest, "Folder to restore into"));
        AddRow(grid, "Disc drive or folder:", _restoreLocation, BrowseFolderButton(_restoreLocation, "Disc drive, mounted image or folder copy of a disc"));
        AddWide(grid, Flow(_restore, _restoreRefresh));
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(_restoreDiscs);
        split.Panel2.Controls.Add(_restoreLog);
        AddWide(grid, split, fill: true);
        _restore.Click += async (_, _) => await RestoreAsync();
        _restoreRefresh.Click += (_, _) => RefreshRestoreList();
        _restoreJob.SelectedIndexChanged += (_, _) => RefreshRestoreList();
        _restoreDest.Leave += (_, _) => RefreshRestoreList();
        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildCatalogTab()
    {
        var page = new TabPage("Catalog");
        var grid = NewGrid();
        var change = new Button { Text = "Use a different file...", AutoSize = true };
        var import = new Button { Text = "Load catalog from a disc...", AutoSize = true };
        AddWide(grid, Note("The catalog is the program's memory: which files are on which disc. Keep it somewhere that is itself backed up or synced " +
                           "(a NAS or cloud folder). Every disc also carries a copy, so it can always be recovered from your most recent disc."));
        AddRow(grid, "Catalog file:", _catalogPath, change);
        AddWide(grid, Flow(import, _forgetRun));
        AddWide(grid, _catalogList, fill: true);
        change.Click += (_, _) => ChangeCatalogLocation();
        import.Click += (_, _) => ImportCatalogFromDisc();
        _forgetRun.Click += (_, _) => ForgetLastRun();
        page.Controls.Add(grid);
        return page;
    }

    // ------------------------------------------------------------------ catalog handling

    private void LoadCatalog()
    {
        _catalogPath.Text = _settings.CatalogPath;
        try
        {
            _catalog = CatalogStore.Load(_settings.CatalogPath);
            _catalogUsable = true;
        }
        catch (Exception ex) when (IsExpected(ex) || ex is System.Text.Json.JsonException)
        {
            _catalog = new BackupCatalog();
            _catalogUsable = false;
            MessageBox.Show(this, $"The catalog file could not be read:\n{_settings.CatalogPath}\n\n{ex.Message}\n\n" +
                "Backups are disabled until you choose a readable catalog file on the Catalog tab " +
                "(a previous version may exist next to it as catalog.json.bak).", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        RefreshCatalogViews();
    }

    private void RefreshCatalogViews()
    {
        string jobText = _job.Text.Length > 0 ? _job.Text : _settings.LastJob ?? "";
        _job.Items.Clear();
        _restoreJob.Items.Clear();
        foreach (var j in _catalog.Jobs)
        {
            _job.Items.Add(j.Name);
            _restoreJob.Items.Add(j.Name);
        }
        _job.Text = jobText;
        if (_source.Text.Length == 0 && _catalog.FindJob(jobText)?.LastSourcePath is { } path) _source.Text = path;
        if (_restoreJob.Items.Count > 0)
            _restoreJob.SelectedIndex = Math.Max(0, _restoreJob.Items.IndexOf(_catalog.FindJob(jobText)?.Name ?? ""));

        _catalogList.BeginUpdate();
        _catalogList.Items.Clear();
        foreach (var job in _catalog.Jobs)
        {
            int currentFiles = job.Files.Count(f => f.RemovedRun is null);
            var header = new ListViewItem(new[] { job.Name, "", $"{currentFiles:N0} current", BackupPlanner.FormatSize(job.Contents.Values.Sum(c => c.Size)), "" })
                { Tag = job, Font = new Font(_catalogList.Font, FontStyle.Bold) };
            _catalogList.Items.Add(header);
            foreach (var run in job.Runs.OrderBy(r => r.Number))
            {
                if (run.Discs.Count == 0)
                    _catalogList.Items.Add(new ListViewItem(new[] { $"    Run {run.Number} (no new files, catalog updated only)", run.DateUtc.ToLocalTime().ToString("d"), "", "", "" }) { Tag = job });
                foreach (var d in run.Discs.OrderBy(d => d.Number))
                    _catalogList.Items.Add(new ListViewItem(new[]
                    {
                        "    " + d.Label, run.DateUtc.ToLocalTime().ToString("d"), d.FileCount.ToString("N0"), BackupPlanner.FormatSize(d.DataBytes),
                        d.VerifiedUtc is { } v ? v.ToLocalTime().ToString("d") : "not yet",
                    }) { Tag = job });
            }
        }
        _catalogList.EndUpdate();
        RefreshDriveLists();
        RefreshRestoreList();
    }

    private void RefreshDriveLists()
    {
        var drives = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
                if (d.DriveType == DriveType.CDRom) drives.Add(d.Name);
        }
        catch (Exception ex) when (IsExpected(ex)) { }
        foreach (var combo in new[] { _verifyLocation, _restoreLocation })
        {
            string text = combo.Text;
            combo.Items.Clear();
            combo.Items.AddRange(drives.Cast<object>().ToArray());
            combo.Text = text.Length > 0 ? text : drives.FirstOrDefault() ?? "";
        }
    }

    private bool SaveCatalog()
    {
        try
        {
            CatalogStore.Save(_catalog, _settings.CatalogPath);
            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            MessageBox.Show(this, "The catalog could not be saved:\n" + ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void ChangeCatalogLocation()
    {
        if (IsBusy) return;
        using var dialog = new SaveFileDialog
        {
            Title = "Catalog file (choose an existing catalog, or a name for a new one)",
            Filter = "Catalog (*.json)|*.json", FileName = Path.GetFileName(_settings.CatalogPath),
            InitialDirectory = Path.GetDirectoryName(_settings.CatalogPath), OverwritePrompt = false,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string chosen = dialog.FileName;
        if (!File.Exists(chosen) && _catalogUsable && _catalog.Jobs.Count > 0)
        {
            var answer = MessageBox.Show(this, "That file does not exist yet.\n\nYes: move the current catalog there.\nNo: start a new, empty catalog there.",
                AppTitle, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return;
            _settings.CatalogPath = chosen;
            if (answer == DialogResult.Yes) { if (!SaveCatalog()) return; }
        }
        else _settings.CatalogPath = chosen;
        _settings.Save();
        InvalidatePlan();
        LoadCatalog();
    }

    private void ImportCatalogFromDisc()
    {
        if (IsBusy || !_catalogUsable) return;
        using var dialog = new FolderBrowserDialog { Description = "Disc (or folder copy of a disc) to load the catalog from", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var updated = CatalogStore.Merge(_catalog, CatalogStore.LoadFromDisc(dialog.SelectedPath));
            if (updated.Count == 0)
            {
                MessageBox.Show(this, "The catalog here is already at least as recent as the one on that disc.", AppTitle);
                return;
            }
            if (!SaveCatalog()) return;
            InvalidatePlan();
            RefreshCatalogViews();
            MessageBox.Show(this, "Loaded from disc: " + string.Join(", ", updated), AppTitle);
        }
        catch (Exception ex) when (IsExpected(ex) || ex is System.Text.Json.JsonException)
        {
            MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ForgetLastRun()
    {
        if (IsBusy || !_catalogUsable) return;
        if (_catalogList.SelectedItems.Count == 0 || _catalogList.SelectedItems[0].Tag is not Job job || job.Runs.Count == 0)
        {
            MessageBox.Show(this, "Select a job (or one of its discs) in the list first.", AppTitle);
            return;
        }
        int last = job.Runs.Max(r => r.Number);
        var answer = MessageBox.Show(this,
            $"Forget run {last} of \"{job.Name}\"?\n\nUse this only if that run's discs were never burned or have been discarded. " +
            "Files first backed up in that run will be backed up again next time.", AppTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (answer != DialogResult.OK) return;
        CatalogUpdater.RemoveLastRun(job);
        if (job.Runs.Count == 0) _catalog.Jobs.Remove(job);
        SaveCatalog();
        InvalidatePlan();
        RefreshCatalogViews();
    }

    // ------------------------------------------------------------------ running work

    private bool IsBusy => _cts != null;

    private void SetBusy(bool busy)
    {
        foreach (TabPage page in _tabs.TabPages)
            foreach (Control c in page.Controls) c.Enabled = !busy;
        _cancel.Enabled = busy;
        if (!busy) _progress.Value = 0;
    }

    private static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException
        or InvalidDataException or InvalidOperationException or NotSupportedException;

    /// <summary>Runs work on a background thread with progress and cancellation. Returns false if it failed or was cancelled.</summary>
    private async Task<bool> RunAsync(Action<IProgress<ProgressInfo>, CancellationToken> work, TextBox log)
    {
        if (IsBusy) return false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);
        var progress = new Progress<ProgressInfo>(ShowProgress);
        try
        {
            await Task.Run(() => work(progress, token), token);
            _status.Text = "Done.";
            return true;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled.";
            Log(log, "Cancelled.");
            return false;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _status.Text = "Stopped because of an error.";
            Log(log, "ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void ShowProgress(ProgressInfo p)
    {
        if (!IsBusy) return;
        string text = p.Stage;
        if (p.Total > 0)
        {
            double fraction = Math.Clamp((double)p.Done / p.Total, 0, 1);
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = (int)(fraction * 1000);
            text += $"  {fraction:P0}";
        }
        else _progress.Style = ProgressBarStyle.Marquee;
        if (!string.IsNullOrEmpty(p.Item)) text += "  -  " + p.Item;
        _status.Text = text;
    }

    private static void Log(TextBox box, string line) => box.AppendText(line + Environment.NewLine);

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (IsBusy)
        {
            if (MessageBox.Show(this, "A task is still running. Stop it and exit?", AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _cts?.Cancel();
        }
        SaveSettings();
    }

    private void SaveSettings()
    {
        _settings.LastJob = _job.Text.Trim();
        _settings.OutputDirectory = _output.Text.Trim();
        _settings.DiscTypeId = _discType.SelectedItem is DiscType d ? d.Id : "custom";
        _settings.CustomDiscMegabytes = (int)_customMb.Value;
        _settings.RecoveryPercent = (double)_recovery.Value;
        _settings.FolderOutput = _outputMode.SelectedIndex == 1;
        _settings.Save();
    }

    // ------------------------------------------------------------------ back up

    private void InvalidatePlan()
    {
        _plan = null;
        _create.Enabled = false;
    }

    private async Task ScanAsync()
    {
        if (!_catalogUsable)
        {
            MessageBox.Show(this, "Choose a readable catalog file on the Catalog tab first.", AppTitle);
            return;
        }
        BackupOptions options;
        try
        {
            if (!Directory.Exists(_source.Text.Trim())) throw new ArgumentException("Choose the folder to back up.");
            options = new BackupOptions
            {
                JobName = BackupPlanner.ValidateJobName(_job.Text),
                SourcePath = _source.Text.Trim(),
                DiscType = _discType.SelectedItem as DiscType ?? DiscTypes.Custom((long)_customMb.Value),
                RecoveryPercent = (double)_recovery.Value,
                RehashAll = _rehash.Checked,
            };
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        InvalidatePlan();
        _backupLog.Clear();
        _images.Items.Clear();
        SaveSettings();
        BackupPlan? plan = null;
        var catalog = _catalog;
        bool ok = await RunAsync((progress, ct) => plan = BackupPlanner.CreatePlan(catalog, options, progress, ct), _backupLog);
        if (!ok || plan is null) return;
        _plan = plan;
        _backupLog.Text = DescribePlan(plan);
        _create.Enabled = true;
        _create.Text = plan.Discs.Count == 0 ? "2. Update catalog" : $"2. Create {plan.Discs.Count} disc{(plan.Discs.Count == 1 ? "" : "s")}";
    }

    private static string DescribePlan(BackupPlan plan)
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);
        var o = plan.Options;
        L($"Job \"{o.JobName.Trim()}\", run {plan.RunNumber}");
        L($"Scanned {plan.ScannedFiles:N0} files ({BackupPlanner.FormatSize(plan.ScannedBytes)}) in {o.SourcePath}");
        L();
        L($"  New or changed, to be written now : {plan.NewFiles:N0} files ({BackupPlanner.FormatSize(plan.NewBytes)})");
        L($"  Already on disc, unchanged        : {plan.UnchangedPaths:N0} files");
        L($"  Already on disc, moved or copied  : {plan.RelocatedPaths:N0} files (catalog updated, nothing rewritten)");
        L($"  No longer in the folder           : {plan.DeletedPaths:N0} files (kept on their discs; noted in the catalog)");
        L();
        if (plan.Discs.Count == 0)
            L(plan.RelocatedPaths + plan.DeletedPaths > 0
                ? "Nothing new to write. Press \"Update catalog\" to record the moved and deleted files."
                : "Everything is already backed up. Nothing to do.");
        else
        {
            L($"Discs needed: {plan.Discs.Count} x {o.DiscType.Name}" + (o.RecoveryPercent > 0 ? $", each with about {o.RecoveryPercent:0.#}% recovery data" : ", no recovery data"));
            foreach (var d in plan.Discs)
                L($"  Disc {d.Number,2}: {d.Files.Count,7:N0} files, {BackupPlanner.FormatSize(d.DataBytes),10} of data, image up to {BackupPlanner.FormatSize(d.PlannedImageBytes)}");
            long largest = plan.Discs.Max(d => d.PlannedImageBytes);
            L();
            L($"Free space needed in the output folder: about {BackupPlanner.FormatSize(largest * 11 / 10)} per disc, " +
              $"{BackupPlanner.FormatSize(plan.Discs.Sum(d => d.PlannedImageBytes))} if you keep all images at once.");
        }
        if (plan.Skipped.Count > 0)
        {
            L();
            L($"WARNING - {plan.Skipped.Count:N0} item(s) will NOT be backed up:");
            if (plan.TooLargeFiles > 0)
                L($"  ({plan.TooLargeFiles:N0} of them are larger than one {o.DiscType.Name}; choose a bigger disc type to include them.)");
            foreach (var s in plan.Skipped.Take(500)) L($"  {s.Path}  -  {s.Reason}");
            if (plan.Skipped.Count > 500) L($"  ... and {plan.Skipped.Count - 500:N0} more");
        }
        return sb.ToString();
    }

    private async Task CreateDiscsAsync()
    {
        if (_plan is not { } plan) return;
        string output = _output.Text.Trim();
        if (plan.Discs.Count > 0 && output.Length == 0)
        {
            MessageBox.Show(this, "Choose a folder to write the disc images to.", AppTitle);
            return;
        }
        if (plan.Discs.Count == 0) output = Path.GetTempPath();
        if (plan.Skipped.Count > 0 && MessageBox.Show(this,
                $"{plan.Skipped.Count:N0} item(s) in the folder will NOT be backed up (they are listed in the plan above).\n\nContinue anyway?",
                AppTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;
        SaveSettings();
        var exec = new ExecuteOptions
        {
            OutputDirectory = output,
            CatalogPath = _settings.CatalogPath,
            Mode = _outputMode.SelectedIndex == 1 ? OutputMode.Folder : OutputMode.IsoImage,
        };
        var catalog = _catalog;
        List<DiscResult>? results = null;
        Log(_backupLog, "");
        Log(_backupLog, plan.Discs.Count == 0 ? "Updating catalog..." : $"Creating {plan.Discs.Count} disc(s) in {output} ...");
        var clock = Stopwatch.StartNew();
        bool ok = await RunAsync((progress, ct) => results = BackupExecutor.Execute(catalog, plan, exec, progress, ct), _backupLog);
        InvalidatePlan();
        _create.Text = "2. Create discs";

        if (!ok)
        {
            // Discs finished before the stop are already recorded; reload to show the true state.
            LoadCatalog();
            Log(_backupLog, "Discs completed before the stop are recorded in the catalog. Scan again to plan the rest.");
            return;
        }
        RefreshCatalogViews();
        if (results is null || results.Count == 0)
        {
            Log(_backupLog, "Catalog updated.");
            return;
        }
        foreach (var r in results)
        {
            _images.Items.Add(r.OutputPath);
            Log(_backupLog, $"  {r.Label}  ->  {r.OutputPath}  ({BackupPlanner.FormatSize(r.ImageBytes)})" +
                            (r.CatalogIncluded ? "" : "  [catalog copy left off this disc: no room]"));
        }
        Log(_backupLog, $"Finished in {clock.Elapsed:h\\:mm\\:ss}.");
        Log(_backupLog, "");
        Log(_backupLog, "NEXT STEPS");
        Log(_backupLog, exec.Mode == OutputMode.IsoImage
            ? " 1. Burn each image to a disc: select it below and press \"Burn selected image\" (Windows Disc Image Burner), or use ImgBurn or similar."
            : " 1. Burn the CONTENTS of each folder to a disc (the disc root should contain README.txt and the Files folder).");
        Log(_backupLog, " 2. Write the label on each disc, e.g. " + results[0].Label + ".");
        Log(_backupLog, " 3. Check each burned disc on the \"Verify disc\" tab before deleting the images.");
    }

    private void BurnSelected()
    {
        if (_images.SelectedItem is not string path || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("isoburn.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            MessageBox.Show(this, "Windows Disc Image Burner could not be started:\n" + ex.Message +
                "\n\nYou can burn the image with any burning program instead.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }
    }

    // ------------------------------------------------------------------ verify

    private async Task VerifyAsync()
    {
        string root = _verifyLocation.Text.Trim();
        if (!Directory.Exists(root))
        {
            MessageBox.Show(this, "Choose the drive that holds the disc (or a folder copy of it).", AppTitle);
            return;
        }
        VerifyResult? result = null;
        bool ok = await RunAsync((progress, ct) => result = DiscVerifier.Verify(root, progress, ct), _verifyLog);
        if (!ok || result is null) return;

        var m = result.Manifest;
        Log(_verifyLog, $"{m.Label}  (job \"{m.Job}\", run {m.Run}, disc {m.Disc} of {m.DiscCount})");
        if (result.Success)
        {
            Log(_verifyLog, $"  PASSED: all {result.OkFiles:N0} files read back correctly.");
            if (_catalogUsable && CatalogUpdater.MarkVerified(_catalog, m, DateTime.UtcNow))
            {
                if (SaveCatalog()) Log(_verifyLog, "  Marked as verified in the catalog.");
                RefreshCatalogViews();
            }
            else Log(_verifyLog, "  (This disc is not in the current catalog, so nothing was recorded.)");
        }
        else
        {
            Log(_verifyLog, $"  FAILED: {result.Problems.Count:N0} of {m.Files.Count:N0} files have problems.");
            foreach (var p in result.Problems.Take(200)) Log(_verifyLog, $"    {p.Path}  -  {p.Reason}");
            if (result.Problems.Count > 200) Log(_verifyLog, $"    ... and {result.Problems.Count - 200:N0} more");
            Log(_verifyLog, "  If this disc was just burned, burn it again on a new disc.");
            Log(_verifyLog, "  If it is an old disc, see README.txt on the disc: copy it to a hard drive and repair it with the recovery-NNN.par2 files.");
        }
        Log(_verifyLog, "");
    }

    // ------------------------------------------------------------------ restore

    private Job? SelectedRestoreJob => _restoreJob.SelectedItem is string name ? _catalog.FindJob(name) : null;

    private void RefreshRestoreList()
    {
        _restoreDiscs.Items.Clear();
        var job = SelectedRestoreJob;
        if (job is null) return;
        string dest = _restoreDest.Text.Trim();
        List<RestoreDiscStatus> status;
        try
        {
            status = RestoreEngine.GetStatus(job, dest.Length > 0 ? dest : Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return;
        }
        foreach (var s in status)
        {
            var run = job.Runs.FirstOrDefault(r => r.Number == s.Run);
            _restoreDiscs.Items.Add(new ListViewItem(new[]
            {
                s.Label, run?.DateUtc.ToLocalTime().ToString("d") ?? "", s.FilesNeeded.ToString("N0"), s.FilesRestored.ToString("N0"),
                s.Complete ? "Done" : "Needed",
            }));
        }
    }

    private async Task RestoreAsync()
    {
        var job = SelectedRestoreJob;
        string dest = _restoreDest.Text.Trim(), root = _restoreLocation.Text.Trim();
        if (job is null) { MessageBox.Show(this, "The catalog has no backup jobs. Load the catalog from your most recent disc on the Catalog tab.", AppTitle); return; }
        if (dest.Length == 0) { MessageBox.Show(this, "Choose a folder to restore into.", AppTitle); return; }
        if (!Directory.Exists(root)) { MessageBox.Show(this, "Choose the drive that holds the disc (or a folder copy of it).", AppTitle); return; }

        RestoreResult? result = null;
        bool ok = await RunAsync((progress, ct) => result = RestoreEngine.RestoreFromDisc(job, root, dest, progress, ct), _restoreLog);
        RefreshRestoreList();
        if (!ok || result is null) return;
        Log(_restoreLog, $"{result.Label}: restored {result.Restored:N0} files, {result.AlreadyPresent:N0} were already there, {result.Problems.Count:N0} problems.");
        foreach (var p in result.Problems.Take(200)) Log(_restoreLog, $"    {p.Path}  -  {p.Reason}");
        if (result.Restored + result.AlreadyPresent + result.Problems.Count == 0)
            Log(_restoreLog, "    (Nothing on this disc is needed: its files were later deleted or replaced by newer versions.)");
        var remaining = RestoreEngine.GetStatus(job, dest).Where(s => !s.Complete).Select(s => s.Label).ToList();
        Log(_restoreLog, remaining.Count == 0 ? "All discs restored. The folder is complete." : "Still needed: " + string.Join(", ", remaining));
        Log(_restoreLog, "");
    }
}
