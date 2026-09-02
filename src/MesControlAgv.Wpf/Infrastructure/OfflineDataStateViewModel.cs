using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MesControlAgv.Wpf.Infrastructure;

/// <summary>Normalized state for offline-capable read/refresh views.</summary>
public enum OfflineDataStateKind
{
    NotLoaded,
    Loading,
    Ready,
    Empty,
    Stale,
    Error,
    Cancelled
}

public sealed record OfflineDataStateTransition(
    OfflineDataStateKind PreviousKind,
    OfflineDataStateKind CurrentKind,
    string Message,
    string? DiagnosticDetail);

/// <summary>
/// Shared presentation state for pages that can render cached/empty data when
/// MES or an Adapter is unavailable. It contains no transport or retry logic.
/// </summary>
public sealed class OfflineDataStateViewModel : INotifyPropertyChanged
{
    private OfflineDataStateKind _kind = OfflineDataStateKind.NotLoaded;
    private string _message = "尚未刷新";
    private bool _hasData;

    public OfflineDataStateKind Kind
    {
        get => _kind;
        private set
        {
            if (!SetField(ref _kind, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsError));
            OnPropertyChanged(nameof(IsStale));
            OnPropertyChanged(nameof(CanRetry));
        }
    }

    public string Message
    {
        get => _message;
        private set => SetField(ref _message, value);
    }

    public bool HasData
    {
        get => _hasData;
        private set => SetField(ref _hasData, value);
    }

    public string StateText => Kind switch
    {
        OfflineDataStateKind.NotLoaded => "未加载",
        OfflineDataStateKind.Loading => "刷新中",
        OfflineDataStateKind.Ready => "已更新",
        OfflineDataStateKind.Empty => "暂无数据",
        OfflineDataStateKind.Stale => "数据过期",
        OfflineDataStateKind.Error => "刷新失败",
        OfflineDataStateKind.Cancelled => "已取消",
        _ => "未知"
    };

    public bool IsBusy => Kind == OfflineDataStateKind.Loading;
    public bool IsError => Kind == OfflineDataStateKind.Error;
    public bool IsStale => Kind == OfflineDataStateKind.Stale;
    public bool CanRetry => !IsBusy;

    public void BeginLoading(string message = "正在刷新...")
    {
        TransitionTo(OfflineDataStateKind.Loading, message);
    }

    public void MarkReady(bool hasData, string message)
    {
        HasData = hasData;
        TransitionTo(hasData ? OfflineDataStateKind.Ready : OfflineDataStateKind.Empty, message);
    }

    public void MarkStale(string message, string? diagnosticDetail = null)
    {
        TransitionTo(OfflineDataStateKind.Stale, message, diagnosticDetail);
    }

    public void MarkError(string message, string? diagnosticDetail = null)
    {
        TransitionTo(OfflineDataStateKind.Error, message, diagnosticDetail);
    }

    public void MarkCancelled(string message = "刷新已取消。")
    {
        TransitionTo(OfflineDataStateKind.Cancelled, message);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<OfflineDataStateTransition>? Transitioned;

    private void TransitionTo(
        OfflineDataStateKind nextKind,
        string message,
        string? diagnosticDetail = null)
    {
        var previousKind = Kind;
        Message = message;
        Kind = nextKind;
        Transitioned?.Invoke(
            this,
            new OfflineDataStateTransition(previousKind, nextKind, message, diagnosticDetail));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
