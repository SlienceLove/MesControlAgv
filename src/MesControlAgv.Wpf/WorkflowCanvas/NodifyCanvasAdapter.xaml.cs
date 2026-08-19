using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.WorkflowCanvas;

/// <summary>
/// Nodify-specific shell for the G1 Spike. No Nodify type is exposed through
/// the graph contracts or the domain editing service.
/// </summary>
public partial class NodifyCanvasAdapter : UserControl, IWorkflowCanvasSurface
{
    private WorkflowCanvasSpikeViewModel? _viewModel;

    public NodifyCanvasAdapter()
    {
        InitializeComponent();
        Unloaded += (_, _) => Detach();
    }

    public void Attach(WorkflowCanvasSpikeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        Detach();
        _viewModel = viewModel;
        _viewModel.FocusNodeRequested += OnFocusNodeRequested;
        DataContext = viewModel;
    }

    public void FitToContent() => Editor.FitToScreen();

    public void FocusNode(Guid nodeId)
    {
        var node = _viewModel?.Nodes.FirstOrDefault(candidate => candidate.Id == nodeId);
        if (node is null) return;

        _viewModel!.SelectNode(nodeId);
        Editor.BringIntoView(new Rect(node.Location.X, node.Location.Y, node.Width, node.Height));
    }

    private void OnFocusNodeRequested(object? sender, Guid nodeId) => FocusNode(nodeId);

    private void Detach()
    {
        if (_viewModel is null) return;
        _viewModel.FocusNodeRequested -= OnFocusNodeRequested;
        _viewModel = null;
    }
}
