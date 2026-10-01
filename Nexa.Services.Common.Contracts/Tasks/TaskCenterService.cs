


namespace Nexa.Services.Tasks;

/// <summary>The reporting surface of one registered task.</summary>
public interface ITaskCenterTask : IDisposable
{
    string TaskId { get; }

    /// <summary>Cancelled when the user cancels the task card (or the service shuts down).</summary>
    CancellationToken CancellationToken { get; }

    void Report(string stage, string detail, double progress, int completedFiles, int totalFiles,
        long speedBytesPerSecond);

    /// <summary>Marks the task finished; the entry stays visible until dismissed.</summary>
    void Complete(string detail = "完成");

    /// <summary>Marks the task canceled after the cancellation token fired.</summary>
    void Canceled();

    /// <summary>Worker has stopped with durable progress retained for a later run.</summary>
    void Paused();

    void Fail(string message);
}
