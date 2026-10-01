namespace Nexa.Platform;

/// <summary>Starts the synchronous GUI portion on its own STA thread, then awaits shutdown.</summary>
public sealed class ApplicationSession
{
    public static Task<int> RunAsync(Func<Task<int>> session)
    {
        ArgumentNullException.ThrowIfNull(session);
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { _ = CompleteAsync(session(), completion); }
            catch (Exception error) { completion.TrySetException(error); }
        })
        { Name = "Nexa UI session" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task CompleteAsync(Task<int> session, TaskCompletionSource<int> completion)
    {
        try { completion.TrySetResult(await session.ConfigureAwait(false)); }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
