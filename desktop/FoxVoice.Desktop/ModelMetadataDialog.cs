using System.Windows;
using System.Windows.Controls;

namespace FoxVoice.Desktop;

internal sealed class ModelMetadataDialog : Window
{
    private readonly TextBox _name;
    private readonly TextBox _author;
    private readonly TextBox _license;
    private readonly TextBox _tags;

    public ModelMetadataDialog(Window owner, string name, string? author, string? license, IReadOnlyList<string> tags)
    {
        Owner = owner;
        Title = UiText.Translate("编辑模型资料");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = owner.FindResource("AppBackground") as System.Windows.Media.Brush;
        Foreground = owner.FindResource("TextPrimary") as System.Windows.Media.Brush;
        _name = new TextBox { Text = name };
        _author = new TextBox { Text = author ?? "" };
        _license = new TextBox { Text = license ?? "" };
        _tags = new TextBox { Text = string.Join(", ", tags) };

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = "模型资料", FontSize = 22, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = "资料只保存在本机 model.json；许可证应以模型发布者声明为准。", Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 18) });
        AddField(root, "名称", _name);
        AddField(root, "作者（可选）", _author);
        AddField(root, "许可证（可选）", _license);
        AddField(root, "标签（英文逗号分隔，最多 20 个）", _tags);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        actions.Children.Add(new Button { Content = "取消", MinWidth = 82, Margin = new Thickness(0, 0, 8, 0), IsCancel = true });
        var save = new Button { Content = "保存", MinWidth = 82, IsDefault = true };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text))
            {
                MessageBox.Show(this, UiText.Translate("模型名称不能为空。"), UiText.Translate("模型资料"), MessageBoxButton.OK, MessageBoxImage.Warning);
                _name.Focus();
                return;
            }
            DialogResult = true;
        };
        actions.Children.Add(save);
        root.Children.Add(actions);
        Content = root;
        UiText.Apply(root, UiText.CurrentLanguage);
    }

    public string ModelName => _name.Text;
    public string Author => _author.Text;
    public string License => _license.Text;
    public string Tags => _tags.Text;

    private static void AddField(Panel panel, string label, Control input)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 5), Opacity = 0.75 });
        panel.Children.Add(input);
    }
}
