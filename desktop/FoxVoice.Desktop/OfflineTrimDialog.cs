using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FoxVoice.Desktop;

internal sealed class OfflineTrimDialog : Window
{
    private readonly Canvas _waveform = new() { Height = 120, Background = new SolidColorBrush(Color.FromRgb(18, 20, 31)) };
    private readonly Slider _start = new() { Minimum = 0, TickFrequency = 100, IsSnapToTickEnabled = false };
    private readonly Slider _end = new() { Minimum = 0, TickFrequency = 100, IsSnapToTickEnabled = false };
    private readonly TextBlock _selection = new();
    private readonly IReadOnlyList<double> _peaks;
    private readonly Action<long, long> _preview;

    public long StartMs => (long)Math.Round(_start.Value);
    public long EndMs => (long)Math.Round(_end.Value);

    public OfflineTrimDialog(Window owner, string path, double durationMs, IReadOnlyList<double> peaks, Action<long, long> preview)
    {
        Owner = owner;
        Title = "离线音频裁剪";
        Width = 680;
        Height = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = new SolidColorBrush(Color.FromRgb(12, 14, 24));
        Foreground = Brushes.White;
        _peaks = peaks;
        _preview = preview;
        _start.Maximum = durationMs;
        _end.Maximum = durationMs;
        _end.Value = durationMs;

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(path), FontSize = 19, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = "选择需要变声的单轨区间", Foreground = Brushes.LightGray, Margin = new Thickness(0, 5, 0, 14) });
        root.Children.Add(_waveform);
        root.Children.Add(new TextBlock { Text = "开始位置", Margin = new Thickness(0, 15, 0, 2) });
        root.Children.Add(_start);
        root.Children.Add(new TextBlock { Text = "结束位置", Margin = new Thickness(0, 9, 0, 2) });
        root.Children.Add(_end);
        root.Children.Add(_selection);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
        var previewButton = new Button { Content = "试听所选原声", Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var confirm = new Button { Content = "转换所选区间", Padding = new Thickness(13, 7, 13, 7), IsDefault = true };
        previewButton.Click += (_, _) => { NormalizeRange(); _preview(StartMs, EndMs); };
        confirm.Click += (_, _) => { NormalizeRange(); DialogResult = true; };
        buttons.Children.Add(previewButton);
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        root.Children.Add(buttons);
        Content = root;

        _start.ValueChanged += (_, _) => { if (_start.Value >= _end.Value) _start.Value = Math.Max(0, _end.Value - 1); UpdateSelection(); };
        _end.ValueChanged += (_, _) => { if (_end.Value <= _start.Value) _end.Value = Math.Min(_end.Maximum, _start.Value + 1); UpdateSelection(); };
        _waveform.SizeChanged += (_, _) => DrawWaveform();
        Loaded += (_, _) => { DrawWaveform(); UpdateSelection(); };
    }

    private void NormalizeRange()
    {
        if (EndMs <= StartMs) _end.Value = Math.Min(_end.Maximum, StartMs + 1);
    }

    private void UpdateSelection()
    {
        _selection.Text = $"{StartMs / 1000.0:N2} 秒 — {EndMs / 1000.0:N2} 秒  ·  {(EndMs - StartMs) / 1000.0:N2} 秒";
        _selection.Foreground = Brushes.LightGray;
        _selection.Margin = new Thickness(0, 8, 0, 0);
    }

    private void DrawWaveform()
    {
        _waveform.Children.Clear();
        if (_peaks.Count == 0 || _waveform.ActualWidth <= 0) return;
        var center = _waveform.ActualHeight / 2;
        var step = _waveform.ActualWidth / _peaks.Count;
        for (var index = 0; index < _peaks.Count; index++)
        {
            var height = Math.Max(1, Math.Clamp(_peaks[index], 0, 1) * (center - 4));
            _waveform.Children.Add(new Line
            {
                X1 = index * step + step / 2,
                X2 = index * step + step / 2,
                Y1 = center - height,
                Y2 = center + height,
                Stroke = new SolidColorBrush(Color.FromRgb(143, 102, 255)),
                StrokeThickness = Math.Max(1, step * 0.65)
            });
        }
    }
}
