using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.WorkflowCanvas;

/// <summary>
/// WPF-only boundary between a workflow designer and its diagram control.
/// A replacement diagram library only needs another implementation of this
/// surface; Contracts, Domain and MES remain independent from the control.
/// </summary>
internal interface IWorkflowCanvasSurface
{
    void Attach(WorkflowCanvasSpikeViewModel viewModel);

    void Detach();

    void FitToContent();

    void FocusNode(Guid nodeId);

    void FocusEdge(Guid edgeId);
}
