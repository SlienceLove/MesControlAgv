using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Views;

public partial class ExperimentFlowEditorView : UserControl
{
    private bool _isEventHandlerAttached = false;

    public ExperimentFlowEditorView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isEventHandlerAttached && DataContext is ExperimentFlowEditorViewModel viewModel)
        {
            viewModel.CreateNodeRequested += OnCreateNodeRequested;
            _isEventHandlerAttached = true;
        }
    }

    private void OnCreateNodeRequested(object? sender, CreateNodeRequestEventArgs e)
    {
        var dialog = new NodeConfigDialog
        {
            Title = $"创建{GetNodeTypeDisplayName(e.NodeType)}",
            Owner = Window.GetWindow(this),
            NodeTitle = e.NodeTitle,
            NodeDescription = string.Empty,
            AvailableNodes = e.ExistingNodes
                .Select(n => new NodeOption
                {
                    Id = n.Id,
                    Title = n.Title,
                    Type = n.Type
                })
                .ToList()
        };

        if (dialog.ShowDialog() == true)
        {
            e.NodeTitle = dialog.NodeTitle;
            e.NodeDescription = dialog.NodeDescription;
            e.PreviousNodeId = dialog.SelectedPreviousNodeId;
            e.NextNodeId = dialog.SelectedNextNodeId;
            e.Confirmed = true;
        }
        else
        {
            e.Confirmed = false;
        }
    }

    private static string GetNodeTypeDisplayName(string nodeType) => nodeType switch
    {
        "Start" => "开始节点",
        "Move" => "AGV 移动节点",
        "Pickup" => "取货节点",
        "Dropoff" => "放货节点",
        "InstrumentOperation" => "仪器操作节点",
        "DataImport" => "数据导入节点",
        "Wait" => "等待节点",
        "ParallelGateway" => "并行网关",
        "End" => "结束节点",
        _ => "节点"
    };
}
