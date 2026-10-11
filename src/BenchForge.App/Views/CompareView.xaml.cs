using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BenchForge.App.ViewModels;
using BenchForge.Core.Reporting;

namespace BenchForge.App.Views;

/// <summary>Builds the comparison table in code because the number of columns (runs) varies.</summary>
public partial class CompareView : UserControl
{
    private CompareViewModel? _vm;

    public CompareView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.Loaded -= Build;
            _vm = DataContext as CompareViewModel;
            if (_vm != null) { _vm.Loaded += Build; Build(); }
        };
    }

    private void Build()
    {
        if (_vm == null) return;
        var res = Application.Current.Resources;
        Table.Children.Clear();
        Table.ColumnDefinitions.Clear();
        Table.RowDefinitions.Clear();
        Table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        foreach (var _ in _vm.Runs) Table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });

        void Add(UIElement e, int row, int col) { Grid.SetRow(e, row); Grid.SetColumn(e, col); Table.Children.Add(e); }
        Table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < _vm.Runs.Count; i++)
        {
            var r = _vm.Runs[i];
            var link = new Hyperlink(new Run(r.DisplayName)) { Foreground = (Brush)res["Fg"], FontWeight = FontWeights.SemiBold };
            var run = r;
            link.Click += (_, _) => _vm.OpenRunCommand.Execute(run);
            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            head.Children.Add(new TextBlock(link) { TextWrapping = TextWrapping.Wrap });
            head.Children.Add(new TextBlock { Text = $"{r.StartTime:yyyy-MM-dd HH:mm} · {r.ImportedFrom ?? r.MachineName}", Foreground = (Brush)res["Muted"], FontSize = 11.5 });
            head.Children.Add(new TextBlock { Text = HtmlReport.StatusText(r.Status), Foreground = (Brush)new Controls.StatusToBrush().Convert(r.Status, typeof(Brush), null!, null!), FontWeight = FontWeights.SemiBold });
            Add(head, 0, i + 1);
        }
        string? lastGroup = null;
        int row = 1;
        foreach (var cr in _vm.Rows)
        {
            if (cr.Group != lastGroup)
            {
                Table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Add(new TextBlock { Text = cr.Group, Foreground = (Brush)res["Accent"], FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) }, row++, 0);
                lastGroup = cr.Group;
            }
            Table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Add(new TextBlock { Text = cr.Name, Margin = new Thickness(0, 3, 10, 3), TextWrapping = TextWrapping.Wrap }, row, 0);
            for (int i = 0; i < cr.Cells.Count; i++)
            {
                var c = cr.Cells[i];
                var tb = new TextBlock { Margin = new Thickness(0, 3, 0, 3), FontFamily = (FontFamily)res["MonoFont"] };
                tb.Inlines.Add(new Run(c.Text));
                if (c.Delta != null) tb.Inlines.Add(new Run("  " + c.Delta) { Foreground = (Brush)res[c.Better ? "Ok" : "Bad"], FontWeight = FontWeights.SemiBold });
                Add(tb, row, i + 1);
            }
            row++;
        }
    }
}
