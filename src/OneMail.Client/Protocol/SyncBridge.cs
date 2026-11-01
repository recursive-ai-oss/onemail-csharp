namespace RecursiveAI.OneMail.Client.Protocol;

/// <summary>
/// Runs the shared implementation of an operation synchronously. Every operation is written once as an asynchronous method that takes
/// a flag; with the flag set it never yields, so the value task has already completed and this only unwraps it.
/// </summary>
internal static class SyncBridge
{
    public static T Run<T>(ValueTask<T> task) => task.AsTask().GetAwaiter().GetResult();

    public static void Run(ValueTask task) => task.AsTask().GetAwaiter().GetResult();
}
