using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace ShortcutManager;

/// <summary>编辑条目路径的小对话框。确定后由调用方校验并写回。</summary>
public partial class PathEditDialog : Window
{
    public string PathText => PathBox.Text;

    public PathEditDialog(string currentPath)
    {
        InitializeComponent();
        PathBox.Text = currentPath;
        Loaded += (_, _) =>
        {
            PathBox.Focus();
            PathBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DialogResult = true;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择文件",
            Filter = "所有文件|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true)
            PathBox.Text = dlg.FileName;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择文件夹" };
        if (dlg.ShowDialog(this) == true)
            PathBox.Text = dlg.FolderName;
    }
}
