using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.WorkflowCanvas;

/// <summary>
/// Nodify-specific shell for the G1 Spike. No Nodify type is exposed through
/// the graph contracts or the domain editing service.
/// </summary>
public partial class NodifyCanvasAdapter : UserControl, IWorkflowCanvasSurface
{
    private WorkflowCanvasSpikeViewModel? _viewModel;
    private WorkflowCanvasSpikeViewModel? _requestedViewModel;
    private readonly DispatcherTimer _viewportCommitTimer;
    private bool _applyingViewport;

    public NodifyCanvasAdapter()
    {
        InitializeComponent();
        _viewportCommitTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _viewportCommitTimer.Tick += CommitViewport;
        Editor.ViewportUpdated += EditorViewportUpdated;
        Loaded += (_, _) => AttachRequestedViewModel();
        Unloaded += (_, _) => DetachSubscriptions();
    }

    public void Attach(WorkflowCanvasSpikeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _requestedViewModel = viewModel;
        AttachRequestedViewModel();
    }

    private void AttachRequestedViewModel()
    {
        if (_requestedViewModel is null) return;
        DetachSubscriptions();
        var viewModel = _requestedViewModel;
        _viewModel = viewModel;
        _viewModel.FocusNodeRequested += OnFocusNodeRequested;
        _viewModel.PropertyChanged += ViewModelPropertyChanged;
        DataContext = viewModel;
        ApplyViewport(viewModel.Document.Viewport);
    }

    public Point GetGraphLocation(DragEventArgs args) => Editor.GetLocationInsideEditor(args);

    public void FitToContent() => Editor.FitToScreen();

    public void FocusNode(Guid nodeId)
    {
        var node = _viewModel?.Nodes.FirstOrDefault(candidate => candidate.Id == nodeId);
        if (node is null) return;

        _viewModel!.SelectNode(nodeId);
        Editor.BringIntoView(new Rect(node.Location.X, node.Location.Y, node.Width, node.Height));
    }

    public void FocusEdge(Guid edgeId)
    {
        var edge = _viewModel?.Connections.FirstOrDefault(candidate => candidate.Id == edgeId);
        if (edge is null) return;

        _viewModel!.SelectedNodes.Clear();
        _viewModel.SelectedConnection = edge;
        var midpoint = edge.MidPoint;
        Editor.BringIntoView(new Rect(midpoint.X - 48, midpoint.Y - 48, 96, 96));
    }

    private void OnFocusNodeRequested(object? sender, Guid nodeId) => FocusNode(nodeId);

    public void Detach()
    {
        _requestedViewModel = null;
        DetachSubscriptions();
        DataContext = null;
    }

    private void DetachSubscriptions()
    {
        _viewportCommitTimer.Stop();
        if (_viewModel is null) return;
        _viewModel.FocusNodeRequested -= OnFocusNodeRequested;
        _viewModel.PropertyChanged -= ViewModelPropertyChanged;
        _viewModel = null;
    }

    private void EditorViewportUpdated(object? sender, RoutedEventArgs e)
    {
        if (_applyingViewport || _viewModel is null) return;
        _viewportCommitTimer.Stop();
        _viewportCommitTimer.Start();
    }

    private void CommitViewport(object? sender, EventArgs e)
    {
        _viewportCommitTimer.Stop();
        _viewModel?.UpdateViewport(
            Editor.ViewportLocation.X,
            Editor.ViewportLocation.Y,
            Editor.ViewportZoom);
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowCanvasSpikeViewModel.Document) && _viewModel is not null)
            ApplyViewport(_viewModel.Document.Viewport);
    }

    private void ApplyViewport(WorkflowCanvasViewport viewport)
    {
        _applyingViewport = true;
        try
        {
            Editor.ViewportLocation = new Point(viewport.X, viewport.Y);
            Editor.ViewportZoom = viewport.Zoom;
        }
        finally
        {
            _applyingViewport = false;
        }
    }
}
