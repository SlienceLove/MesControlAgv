namespace MesControlAgv.Wpf.Services;

/// <summary>
/// Explicit workflow-run binding supplied at process startup.  The binding is
/// intentionally an execution id or a request id; there is no "latest run"
/// discovery fallback, because that could attach the operator to another
/// physical run.
/// </summary>
public sealed record WorkflowRunStartupBinding(
    Guid? ExecutionId,
    Guid? RequestId,
    string? Error = null)
{
    public bool IsSpecified => ExecutionId.HasValue || RequestId.HasValue;
    public bool IsValid => string.IsNullOrWhiteSpace(Error) &&
                           (ExecutionId.HasValue ^ RequestId.HasValue);

    public static WorkflowRunStartupBinding None { get; } = new(null, null);

    public static WorkflowRunStartupBinding Parse(
        IReadOnlyList<string>? args,
        Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var executionText = ReadArgument(args, "--workflow-execution-id") ??
                            readEnvironment("WPF_INITIAL_WORKFLOW_EXECUTION_ID") ??
                            readEnvironment("WORKFLOW_EXECUTION_ID");
        var requestText = ReadArgument(args, "--workflow-request-id") ??
                          readEnvironment("WPF_INITIAL_WORKFLOW_REQUEST_ID") ??
                          readEnvironment("WORKFLOW_REQUEST_ID");

        if (string.IsNullOrWhiteSpace(executionText) && string.IsNullOrWhiteSpace(requestText))
            return None;
        if (!string.IsNullOrWhiteSpace(executionText) && !string.IsNullOrWhiteSpace(requestText))
        {
            return new WorkflowRunStartupBinding(
                null,
                null,
                "同时提供 workflow execution ID 和 request ID；请只指定一个显式绑定目标。");
        }

        if (!string.IsNullOrWhiteSpace(executionText))
        {
            return Guid.TryParse(executionText.Trim(), out var executionId) && executionId != Guid.Empty
                ? new WorkflowRunStartupBinding(executionId, null)
                : new WorkflowRunStartupBinding(null, null, "workflow execution ID 不是有效的非空 GUID。");
        }

        return Guid.TryParse(requestText!.Trim(), out var requestId) && requestId != Guid.Empty
            ? new WorkflowRunStartupBinding(null, requestId)
            : new WorkflowRunStartupBinding(null, null, "workflow request ID 不是有效的非空 GUID。");
    }

    private static string? ReadArgument(IReadOnlyList<string>? args, string name)
    {
        if (args is null || args.Count == 0) return null;
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
                return string.Empty;
            return args[index + 1];
        }
        return null;
    }
}
