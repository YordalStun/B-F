using System.Collections.ObjectModel;
using BenchForge.Core.Events;
using BenchForge.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BenchForge.App.ViewModels;

public sealed record TimeRangeOption(string Label, double? Days);

public sealed partial class EventsViewModel : PageViewModel
{
    public EventsViewModel(MainViewModel main) : base(main, "Event Viewer", "")
    {
        _range = Ranges[2];
    }

    public EventPreset[] Presets { get; } = EventPresets.All;
    public string[] Examples { get; } = { "all bluescreens", "crashes last week", "gpu driver crashes", "whea hardware errors", "unexpected shutdowns last month", "disk errors", "who restarted my pc", "event 41" };
    public TimeRangeOption[] Ranges { get; } = { new("Last 24 hours", 1), new("Last 7 days", 7), new("Last 30 days", 30), new("Last 90 days", 90), new("Last year", 365), new("All time", null) };
    public ObservableCollection<EventEntry> Events { get; } = new();

    [ObservableProperty] private string _askText = "";
    [ObservableProperty] private TimeRangeOption _range;
    [ObservableProperty] private bool _critical = true;
    [ObservableProperty] private bool _error = true;
    [ObservableProperty] private bool _warning = true;
    [ObservableProperty] private bool _information;
    [ObservableProperty] private string _textFilter = "";
    [ObservableProperty] private string _description = "Ask in plain words, or pick a category.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private EventEntry? _selected;
    [ObservableProperty] private string _explanation = "";
    private EventQuerySpec? _lastSpec;

    partial void OnSelectedChanged(EventEntry? value) => Explanation = value == null ? "" : EventInterpreter.Explain(value);

    [RelayCommand]
    private async Task Ask(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)) AskText = text;
        if (string.IsNullOrWhiteSpace(AskText)) return;
        var spec = EventQueryParser.Parse(AskText);
        await Run(spec);
    }

    [RelayCommand]
    private async Task Preset(EventPreset? preset)
    {
        if (preset == null) return;
        var spec = preset.ToSpec(Range.Days is double d ? DateTime.Now.AddDays(-d) : null);
        if (preset.Levels == null) spec.Levels.Clear();
        spec.Description = $"Showing {preset.Title.ToLowerInvariant()} — {Range.Label.ToLowerInvariant()}";
        await Run(spec);
    }

    [RelayCommand]
    private async Task Filter()
    {
        // Free filter: chosen levels + time + text across System and Application.
        var spec = new EventQuerySpec { Since = Range.Days is double d ? DateTime.Now.AddDays(-d) : null, Text = string.IsNullOrWhiteSpace(TextFilter) ? null : TextFilter };
        if (Critical) spec.Levels.Add(1);
        if (Error) spec.Levels.Add(2);
        if (Warning) spec.Levels.Add(3);
        if (Information) spec.Levels.Add(4);
        spec.Description = $"Showing {string.Join(", ", new[] { Critical ? "critical" : null, Error ? "errors" : null, Warning ? "warnings" : null, Information ? "information" : null }.Where(x => x != null))}" +
                           $"{(spec.Text != null ? $" containing \"{spec.Text}\"" : "")} — {Range.Label.ToLowerInvariant()}";
        await Run(spec);
    }

    private async Task Run(EventQuerySpec spec)
    {
        IsBusy = true;
        _lastSpec = spec;
        Description = spec.Description + " …";
        var list = await Task.Run(() => Main.S.Events.Query(spec, 1000));
        Events.Clear();
        foreach (var e in list) Events.Add(e);
        Description = $"{spec.Description} — {list.Count} event{(list.Count == 1 ? "" : "s")}{(list.Count == 1000 ? " (first 1000)" : "")}";
        Selected = Events.FirstOrDefault();
        IsBusy = false;
    }

    [RelayCommand]
    private void AskAi()
    {
        if (Events.Count == 0) return;
        Main.AskAi("Ask AI about these events", Array.Empty<RunResult>(), events: Events.Take(80).ToList(),
            question: $"These are Windows events ({_lastSpec?.Description}). What do they tell you about problems with this PC, and what should I do?");
    }
}
