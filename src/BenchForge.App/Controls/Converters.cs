using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BenchForge.Core.Model;

namespace BenchForge.App.Controls;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value switch { bool x => x, int i => i != 0, string s => !string.IsNullOrWhiteSpace(s), null => false, System.Collections.ICollection col => col.Count > 0, _ => true };
        return (b ^ Invert) ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class SeverityToBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        Severity.Critical => Application.Current.Resources["Bad"],
        Severity.Warning => Application.Current.Resources["Warn"],
        _ => Application.Current.Resources["Ok"],
    };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class StatusToBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        RunStatus.Completed => Application.Current.Resources["Ok"],
        RunStatus.Crashed or RunStatus.CompletedWithErrors or RunStatus.Aborted => Application.Current.Resources["Bad"],
        RunStatus.Cancelled => Application.Current.Resources["Muted"],
        RunStatus.Running => Application.Current.Resources["Accent"],
        "Passed" => Application.Current.Resources["Ok"],
        "Errors" => Application.Current.Resources["Bad"],
        "Running" => Application.Current.Resources["Accent"],
        _ => Application.Current.Resources["Muted"],
    };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class StatusToText : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is RunStatus s ? Core.Reporting.HtmlReport.StatusText(s) : value?.ToString() ?? "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Turns a 0..1 fraction into a width relative to the converter parameter (max width).</summary>
public sealed class FractionToWidth : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double f = value is double d ? d : 0;
        double max = p is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var m) ? m : 200;
        return Math.Clamp(f, 0, 1) * max;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Colours a temperature: green below 70, amber to 85, red above.</summary>
public sealed class TempToBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double v = value is double d ? d : 0;
        return v >= 85 ? Application.Current.Resources["Bad"] : v >= 70 ? Application.Current.Resources["Warn"] : Application.Current.Resources["Ok"];
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
