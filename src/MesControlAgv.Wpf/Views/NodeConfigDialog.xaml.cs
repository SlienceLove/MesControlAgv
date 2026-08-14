using System.Windows;
using System.Windows.Controls;
using System.Linq;
using System.Collections.Generic;

namespace MesControlAgv.Wpf.Views;

/// <summary>
/// 节点配置对话框
/// </summary>
public partial class NodeConfigDialog : Window
{
    public NodeConfigDialog()
    {
        InitializeComponent();
    }

    public string NodeTitle
    {
        get => TitleTextBox.Text;
        set => TitleTextBox.Text = value;
    }

    public string NodeDescription
    {
        get => DescriptionTextBox.Text;
        set => DescriptionTextBox.Text = value;
    }

    public List<NodeOption> AvailableNodes
    {
        set
        {
            PreviousNodeComboBox.ItemsSource = value;
            NextNodeComboBox.ItemsSource = value;
        }
    }

    public Guid? SelectedPreviousNodeId
    {
        get => (PreviousNodeComboBox.SelectedItem as NodeOption)?.Id;
        set
        {
            if (value.HasValue && PreviousNodeComboBox.ItemsSource != null)
            {
                var item = PreviousNodeComboBox.ItemsSource.Cast<NodeOption>()
                    .FirstOrDefault(n => n.Id == value.Value);
                PreviousNodeComboBox.SelectedItem = item;
            }
        }
    }

    public Guid? SelectedNextNodeId
    {
        get => (NextNodeComboBox.SelectedItem as NodeOption)?.Id;
        set
        {
            if (value.HasValue && NextNodeComboBox.ItemsSource != null)
            {
                var item = NextNodeComboBox.ItemsSource.Cast<NodeOption>()
                    .FirstOrDefault(n => n.Id == value.Value);
                NextNodeComboBox.SelectedItem = item;
            }
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NodeTitle))
        {
            MessageBox.Show("请输入节点名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            TitleTextBox.Focus();
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

/// <summary>
/// 节点选项（用于下拉框）
/// </summary>
public sealed class NodeOption
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;

    public override string ToString() => $"{Title} ({Type})";
}
