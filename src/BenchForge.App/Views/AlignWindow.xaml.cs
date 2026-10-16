using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BenchForge.App.Platform;
using BenchForge.Core.Model;
using BenchForge.Core.Persistence;
using BenchForge.Core.Storage;

namespace BenchForge.App.Views;

public sealed record PartitionRow(PartitionEntry P, AlignVerdict Verdict, string Title, string Sub, string Disk, string Start, string Status, Brush StatusBrush);

/// <summary>Shows every partition's alignment; fixes data partitions itself and guides the user for the Windows partition.</summary>
public partial class AlignWindow : Window
{
    private readonly HardwareInventory? _inv;
    private readonly bool _admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private List<PartitionEntry> _parts = new();
    private readonly HashSet<(int, long)> _wasMisaligned = new();
    private CancellationTokenSource? _cts;
    private bool _busy, _pastPointOfNoReturn;

    public AlignWindow(HardwareInventory? inv)
    {
        InitializeComponent();
        _inv = inv;
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width * 0.95);
        Height = Math.Min(Height, wa.Height * 0.95);
        AdminWarning.Visibility = _admin ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (_, _) => { await Reload(); ShowResume(); };
        Closing += (_, e) =>
        {
            if (!_busy) return;
            if (_pastPointOfNoReturn)
            {
                MessageBox.Show(this, "Please wait — BenchForge is copying your files back. Closing now would leave the drive empty (your files stay safe in the temporary copy).", "Partition alignment", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Cancel = true;
            }
            else _cts?.Cancel();
        };
    }

    private async Task Reload()
    {
        try
        {
            var before = _parts.Where(p => !p.IsAligned).Select(p => (p.DiskNumber, p.Offset)).ToList();
            _parts = await Task.Run(() => PartitionReader.Read(_inv));
            foreach (var b in before) _wasMisaligned.Add(b);
        }
        catch (Exception ex)
        {
            InfoText.Text = "Could not read the partitions: " + ex.Message;
            return;
        }
        var app = AppContext.BaseDirectory;
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var good = (Brush)FindResource("Ok"); var bad = (Brush)FindResource("Bad"); var muted = (Brush)FindResource("Muted");
        PartitionList.ItemsSource = _parts.Select(p =>
        {
            var v = PartitionAlign.Check(p, app, win);
            string sub = p.DriveLetter == null ? (p.IsSystem ? "EFI / system" : p.IsHidden ? "hidden" : "no letter") : $"{p.FileSystem} {p.Label}".Trim();
            string disk = $"Disk {p.DiskNumber} · {p.DiskModel} · {p.PartitionStyle}{(p.MediaType is "SSD" or "HDD" ? " · " + p.MediaType : "")}";
            string start = $"Starts at {p.Offset:N0} bytes · {p.Size / 1e9:0.#} GB";
            string status = p.IsAligned ? "Aligned ✓" : v.CanAutoFix ? "Not aligned — can fix here" : "Not aligned";
            return new PartitionRow(p, v, p.Name, sub, disk, start, status, p.IsAligned ? good : p.MediaType == "HDD" ? muted : bad);
        }).ToList();

        // Something that was misaligned earlier is aligned now (fixed with a partition tool) — say so and re-TRIM it.
        foreach (var p in _parts.Where(p => p.IsAligned && p.DriveLetter != null && _wasMisaligned.Any(w => w.Item1 == p.DiskNumber && Math.Abs(w.Item2 - p.Offset) < 64 * PartitionAlign.MiB)).ToList())
        {
            _wasMisaligned.RemoveWhere(w => w.Item1 == p.DiskNumber && Math.Abs(w.Item2 - p.Offset) < 64 * PartitionAlign.MiB);
            ShowProgress($"{p.Name} is aligned now.");
            if (p.MediaType == "SSD") await Retrim(p.DriveLetter!.Value);
        }

        var misaligned = PartitionList.Items.Cast<PartitionRow>().FirstOrDefault(r => !r.P.IsAligned && r.P.DriveLetter != null)
                         ?? PartitionList.Items.Cast<PartitionRow>().FirstOrDefault(r => !r.P.IsAligned);
        PartitionList.SelectedItem = misaligned;
        if (misaligned == null) ShowInfo(_parts.Count == 0 ? "No partitions found." : "Every partition is aligned ✓ — nothing to do.");
    }

    private void PartitionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PartitionList.SelectedItem is not PartitionRow r) return;
        var p = r.P;
        if (p.IsAligned) { ShowInfo($"{p.Name} starts at {p.Offset:N0} bytes = {p.Offset / 4096:N0} × 4 KB. It is aligned — nothing to do."); return; }
        if (p.MediaType == "HDD")
        {
            ShowGuide(p, "This is a hard drive. Most hard drives use 512-byte sectors where this barely matters — only fix it if it is a newer 'Advanced Format' (4K) drive.");
            return;
        }
        if (!r.Verdict.CanAutoFix || !_admin) { ShowGuide(p, _admin ? r.Verdict.Reason : "Run BenchForge as administrator to fix it here."); return; }

        InfoPanel.Visibility = Visibility.Collapsed; GuidePanel.Visibility = Visibility.Collapsed; AutoPanel.Visibility = Visibility.Visible;
        var used = p.UsedBytes ?? 0;
        // Copy off + read both sides + copy back + read both sides ≈ 6 passes over the data at ~200 MB/s.
        var minutes = Math.Max(2, used * 6 / 200e6 / 60);
        AutoTitle.Text = $"BenchForge can fix {p.Name} itself";
        AutoText.Text = $"It copies the {used / 1e9:0.0} GB of files on {p.Name} to another drive, checks every file, deletes {p.Name} and creates it again aligned (same letter, name and file system), copies the files back and checks every file again. "
                      + $"Takes about {minutes:0} minutes. Nothing is deleted until the copy has been checked, and you are asked once more before that.\n\n"
                      + "Unlike MiniTool or AOMEI, which shift the partition in place, BenchForge works with a checked copy of your files — a power cut can't lose data, but it needs space on another drive.\n\n"
                      + AfterEffects;
        var targets = PartitionAlign.BackupTargets(p, PartitionReader.Volumes(_parts));
        BackupCombo.ItemsSource = targets;
        BackupCombo.SelectedIndex = targets.Count > 0 ? 0 : -1;
        FixButton.Content = $"Fix {p.Name}";
        FixButton.IsEnabled = targets.Count > 0 && !_busy;
        BackupWarning.Text = targets.Count == 0 ? $"No other drive has {(used + PartitionAlign.BackupMargin) / 1e9:0} GB free for the temporary copy. Plug in a USB drive with enough space (not FAT32) and press 'Check again'." : "";
        BackupWarning.Visibility = targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private const string AfterEffects = "Your files, folders, links, permissions, drive letter and name stay the same. Restore points (shadow copies) for this drive and its serial number are reset, and hard-linked files become separate copies.";

    private void BackupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BackupCombo.SelectedItem is BackupVolume v && PartitionList.SelectedItem is PartitionRow r && v.DiskNumber == r.P.DiskNumber)
        {
            BackupWarning.Text = "This drive is on the same disk. That works, but if the disk itself is failing a copy on another disk is safer.";
            BackupWarning.Visibility = Visibility.Visible;
        }
        else if (BackupCombo.SelectedItem != null) BackupWarning.Visibility = Visibility.Collapsed;
    }

    private void ShowInfo(string text)
    {
        InfoText.Text = text;
        InfoPanel.Visibility = Visibility.Visible; AutoPanel.Visibility = Visibility.Collapsed; GuidePanel.Visibility = Visibility.Collapsed;
    }

    private void ShowGuide(PartitionEntry p, string reason)
    {
        GuideTitle.Text = $"How to align {p.Name}";
        GuideReason.Text = reason;
        InfoPanel.Visibility = Visibility.Collapsed; AutoPanel.Visibility = Visibility.Collapsed; GuidePanel.Visibility = Visibility.Visible;
    }

    private async void Fix_Click(object sender, RoutedEventArgs e)
    {
        if (PartitionList.SelectedItem is not PartitionRow r || BackupCombo.SelectedItem is not BackupVolume target) return;
        var p = r.P;
        var msg = $"Fix the alignment of {p.Name}?\n\n"
                + $"1. Copy all files ({(p.UsedBytes ?? 0) / 1e9:0.0} GB) to {target.Letter}: and check every one\n"
                + $"2. Delete {p.Name} and create it again aligned (you'll be asked once more first)\n"
                + $"3. Copy the files back and check every one\n\n"
                + $"Close all programs that use {p.Name}, keep the PC plugged in and don't use {p.Name} until it's done.\n"
                + AfterEffects + "\n\n"
                + "Having your own backup of important files is still wise.";
        if (MessageBox.Show(this, msg, "Partition alignment", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var folder = PartitionAligner.BackupFolderFor(p, $"{target.Letter}:\\");
        await RunJob(aligner => aligner.RunAsync(p, $"{p.DriveLetter}:\\", folder, ConfirmDelete, ConfirmRemoveBackup, _cts!.Token), p, retrimAfter: false);
    }

    /// <param name="retrimAfter">Re-TRIM when done. Not needed after a fix started here: <see cref="Reload"/> notices the partition became aligned and does it.</param>
    private async Task RunJob(Func<PartitionAligner, Task<bool>> work, PartitionEntry? p, bool retrimAfter)
    {
        _busy = true; _pastPointOfNoReturn = false;
        _cts = new CancellationTokenSource();
        FixButton.IsEnabled = false; ResumeButton.IsEnabled = false; RefreshButton.IsEnabled = false; CancelButton.IsEnabled = true;
        Output.Clear();
        ShowProgress("Starting…");
        var aligner = new PartitionAligner(new WindowsAlignOps(), Append, (f, s) => Dispatcher.BeginInvoke(() =>
        {
            Progress.Value = f; StatusText.Text = s;
            // From the delete on, the job must run to the end.
            if (f >= 0.55 && !_pastPointOfNoReturn) { _pastPointOfNoReturn = true; CancelButton.IsEnabled = false; }
        }));
        bool ok;
        try { ok = await work(aligner); }
        catch (Exception ex) { Append("Unexpected error: " + ex.Message); ok = false; }
        _busy = false;
        CancelButton.IsEnabled = false; RefreshButton.IsEnabled = true; ResumeButton.IsEnabled = true;
        StatusText.Text = ok ? "Done — the partition is aligned." : "Stopped — see the details below.";
        if (ok && retrimAfter && p?.DriveLetter is char letter && p.MediaType == "SSD") await Retrim(letter);
        await Reload();
        ShowResume();
    }

    private Task<bool> ConfirmDelete(string text) => Ask(text, MessageBoxImage.Warning);
    private Task<bool> ConfirmRemoveBackup(string text) => Ask(text, MessageBoxImage.Question);
    private Task<bool> Ask(string text, MessageBoxImage icon) =>
        Dispatcher.InvokeAsync(() => MessageBox.Show(this, text, "Partition alignment", MessageBoxButton.YesNo, icon) == MessageBoxResult.Yes).Task;

    private async Task Retrim(char letter)
    {
        Append($"Re-TRIM {letter}: so the SSD knows which blocks are free…");
        await FixRunner.RunProcess("defrag.exe", $"{letter}: /L /U", Append, CancellationToken.None, Encoding.UTF8);
        Append("Run the drive benchmark again to see the difference.");
    }

    private void ShowProgress(string status)
    {
        ProgressPanel.Visibility = Visibility.Visible;
        StatusText.Text = status;
    }

    private void Append(string line) => Dispatcher.BeginInvoke(() =>
    {
        ProgressPanel.Visibility = Visibility.Visible;
        Output.AppendText(line + Environment.NewLine);
        Output.ScrollToEnd();
        try { DataPaths.AppLogWrite("align: " + line); } catch { }
    });

    // ---- Unfinished job from an earlier run ----

    private void ShowResume()
    {
        var job = AlignJob.Load();
        if (job == null || !job.DataOnlyInBackup) { ResumePanel.Visibility = Visibility.Collapsed; return; }
        bool driveBack = DriveReady(job.Letter);
        ResumeText.Text = $"Started {job.Started:g} on {job.Letter}:. Your files are safe in the temporary copy:\n{job.BackupFolder}\n\n"
                        + (driveBack
                            ? $"{job.Letter}: exists again. Press 'Finish restoring' to copy the files back and check them."
                            : $"{job.Letter}: is missing. Open Disk Management, right-click the unallocated space on disk {job.DiskNumber} › New Simple Volume, give it the letter {job.Letter}, then press 'Finish restoring'.");
        ResumeButton.IsEnabled = driveBack && !_busy;
        ResumePanel.Visibility = Visibility.Visible;
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        var job = AlignJob.Load();
        if (job == null || !Directory.Exists(job.BackupFolder)) { MessageBox.Show(this, "The temporary copy folder was not found.", "Partition alignment"); return; }
        await RunJob(a => a.RestoreAsync(job, null, $"{job.Letter}:\\", ConfirmRemoveBackup), _parts.FirstOrDefault(x => x.DriveLetter == job.Letter), retrimAfter: true);
    }

    private static bool DriveReady(char letter) { try { return new DriveInfo($"{letter}:\\").IsReady; } catch { return false; } }

    private void OpenBackup_Click(object sender, RoutedEventArgs e)
    {
        var job = AlignJob.Load();
        if (job != null) Open("explorer.exe", "\"" + job.BackupFolder + "\"");
    }

    private void DiskMgmt_Click(object sender, RoutedEventArgs e) => Open("diskmgmt.msc");
    private void MsInfo_Click(object sender, RoutedEventArgs e) => Open("msinfo32.exe");
    private void MiniTool_Click(object sender, RoutedEventArgs e) => Open("https://www.partitionwizard.com/free-partition-manager.html");
    private void Aomei_Click(object sender, RoutedEventArgs e) => Open("https://www.diskpart.com/free-partition-manager.html");

    private static void Open(string target, string? args = null)
    {
        try { Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true }); } catch { }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) { await Reload(); ShowResume(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
