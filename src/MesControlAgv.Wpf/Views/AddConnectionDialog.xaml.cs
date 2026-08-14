using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Views;

public partial class AddConnectionDialog : Window, INotifyPropertyChanged
{
    private List<ExperimentFlowNode> _availableNodes = [];
    private ExperimentFlowNode? _sourceNode;
    private ExperimentFlowNode? _targetNode;
    private string _condition = string.Empty;

    public AddConnectionDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    public List<ExperimentFlowNode> AvailableNodes
    {
        get => _availableNodes;
        set => SetField(ref _availableNodes, value);
    }

    public ExperimentFlowNode? SourceNode
    {
        get => _sourceNode;
        set => SetField(ref _sourceNode, value);
    }

    public ExperimentFlowNode? TargetNode
    {
        get => _targetNode;
        set => SetField(ref _targetNode, value);
    }

    public string Condition
    {
        get => _condition;
        set => SetField(ref _condition, value);
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (TargetNode == null)
        {
            MessageBox.Show("请选择目标节点", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
