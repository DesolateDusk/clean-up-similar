using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using FileComparer.Domain.Models;

namespace FileComparer.Desktop;

public sealed class DeleteConfirmationWindow : Window
{
    public DeleteConfirmationWindow(DeleteSummary summary)
    {
        Title = "確認移至資源回收筒";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var cancel = new Button { Content = "取消" };
        cancel.Click += (_, _) => Close(false);
        var confirm = new Button
        {
            Content = "確認移至資源回收筒",
            Classes = { "danger" }
        };
        confirm.Click += (_, _) => Close(true);

        Content = new Border
        {
            Padding = new Thickness(24),
            Child = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "即將移動所選檔案",
                        FontSize = 22,
                        FontWeight = Avalonia.Media.FontWeight.Bold
                    },
                    new TextBlock
                    {
                        Text = $"共 {summary.Count.ToString(CultureInfo.CurrentCulture)} 個檔案，" +
                               $"合計 {FileSizeFormatter.Format(summary.TotalBytes)}。檔案會送至 Windows " +
                               "資源回收筒，可由系統資源回收筒還原。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, confirm }
                    }
                }
            }
        };
    }
}
