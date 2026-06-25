using System.Collections.Concurrent;
using System.Net.Sockets;
using Dalamud.Plugin.Services;

namespace dad.Services;

internal sealed class DadBackgroundTaskObserver : IDisposable
{
    private readonly ConcurrentDictionary<Task, string> activeTasks = new();
    private readonly IPluginLog log;
    private readonly string componentName;
    private bool disposed;

    public DadBackgroundTaskObserver(IPluginLog log, string componentName)
    {
        this.log = log;
        this.componentName = string.IsNullOrWhiteSpace(componentName) ? "background" : componentName;
    }

    public void Track(Task task, string operationName)
    {
        if (task.IsCompleted)
        {
            ObserveCompletedTask(task, operationName);
            return;
        }

        activeTasks[task] = operationName;
        _ = task.ContinueWith(
            static (completedTask, state) =>
            {
                try
                {
                    var (observer, operation) = ((DadBackgroundTaskObserver Observer, string Operation))state!;
                    observer.ObserveCompletedTask(completedTask, operation);
                }
                catch
                {
                    // The continuation exists only to observe the original task. Never let
                    // observer failures create a second unobserved background fault.
                }
            },
            (this, operationName),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        disposed = true;
    }

    private void ObserveCompletedTask(Task task, string operationName)
    {
        try
        {
            if (task.IsCanceled)
                return;

            var exception = task.Exception?.Flatten();
            if (exception == null)
                return;

            var unexpected = exception.InnerExceptions
                .Where(static ex => !IsExpectedShutdownException(ex))
                .ToList();
            if (unexpected.Count == 0)
            {
                if (!disposed)
                    log.Debug("[dad] {Component} task '{Operation}' ended during cancellation.", componentName, operationName);
                return;
            }

            log.Debug(
                new AggregateException(unexpected),
                "[dad] {Component} task '{Operation}' ended with an error.",
                componentName,
                operationName);
        }
        finally
        {
            activeTasks.TryRemove(task, out _);
        }
    }

    public static bool IsExpectedShutdownException(Exception exception)
    {
        if (exception is AggregateException aggregate)
            return aggregate.Flatten().InnerExceptions.All(IsExpectedShutdownException);

        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is OperationCanceledException or ObjectDisposedException)
                return true;

            if (current is SocketException socketException && IsExpectedShutdownSocketError(socketException))
                return true;
        }

        return false;
    }

    private static bool IsExpectedShutdownSocketError(SocketException exception)
        => exception.ErrorCode == 995 ||
           exception.SocketErrorCode is SocketError.OperationAborted
               or SocketError.Interrupted
               or SocketError.ConnectionAborted
               or SocketError.ConnectionReset;
}
