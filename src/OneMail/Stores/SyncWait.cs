namespace RecursiveAI.OneMail.Stores;

/// <summary>
/// Completes the value tasks of the shared sync/async implementations. The synchronous entry points run the same code as
/// the asynchronous ones with reads that never suspend, so the task is always complete when it returns.
/// </summary>
internal static class SyncWait
{
    public static void Complete(ValueTask task)
    {
        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
        }
        else
        {
            task.AsTask().GetAwaiter().GetResult();
        }
    }

    public static T Result<T>(ValueTask<T> task) =>
        task.IsCompleted ? task.GetAwaiter().GetResult() : task.AsTask().GetAwaiter().GetResult();
}
