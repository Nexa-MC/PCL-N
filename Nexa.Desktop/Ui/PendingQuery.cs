using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

/// <summary>Polls a query during frame preparation without waiting or throwing a task fault.</summary>
internal readonly struct PendingQuery<T>(Task<XsrResult<T>> task)
{
    public bool TryRead([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out XsrResult<T>? result)
    {
        if (task.IsCompletedSuccessfully) { result = task.Result; return true; }
        if (task.IsFaulted) _ = task.Exception;
        result = null;
        return false;
    }
}

internal static class PendingQuery
{
    public static bool Succeeded<T>(Task<XsrResult<T>> task) => new PendingQuery<T>(task).TryRead(out var result) && result.IsSuccess;
    public static bool Succeeded<T>(ValueTask<XsrResult<T>> task) => task.IsCompletedSuccessfully && task.Result.IsSuccess;
    public static bool Succeeded(Task<XsrResult> task) => task.IsCompletedSuccessfully && task.Result.IsSuccess;
    public static bool Succeeded(ValueTask<XsrResult> task) => task.IsCompletedSuccessfully && task.Result.IsSuccess;
}
