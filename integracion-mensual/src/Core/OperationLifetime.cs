using System.Diagnostics;

namespace SistemaMarcaciones.F13.Integrated.Native;

public sealed record ShutdownDrainResult(bool Complete,int ActiveTasks,int OwnedChildren,string[] Errors,
    int OpenOperations=0,int PendingAttempts=0);

/// <summary>Shared lifetime of own host/editor work. No OS hook, engine rule or storage change.</summary>
public static class OperationLifetime
{
    sealed record Frame(CancellationToken Token,bool ClosingWork,int CommitDepth=0);
    sealed record Entry(long Id,TaskCompletionSource Completion);
    sealed class RestoreFrame(Frame? previous):IDisposable
    {bool disposed;public void Dispose(){if(disposed)return;Current.Value=previous;disposed=true;}}
    static readonly object Sync=new();
    static readonly AsyncLocal<Frame?> Current=new();
    static readonly Dictionary<long,Entry> Work=new();
    static readonly List<string> ObservedErrors=new();
    static CancellationTokenSource normal=new();
    static long serial;
    static bool closing;
    static OperationLifetime()=>OwnedLaunchBoundary.BindTaskCount(()=>ActiveTaskCount);
    public static bool IsClosing {get{lock(Sync)return closing;}}
    public static int ActiveTaskCount {get{lock(Sync)return Work.Count;}}
    public static CancellationToken CurrentToken=>Current.Value?.Token??CancellationToken.None;
    public static void Checkpoint()
    {
        var frame=Current.Value;
        frame?.Token.ThrowIfCancellationRequested();
        lock(Sync)if(closing&&frame is not {ClosingWork:true}&&frame is not {CommitDepth:>0})
            throw new OperationCanceledException("SHUTDOWN_REJECTS_NEW_WORK",normal.Token);
    }
    public static IDisposable EnterCommit()
    {
        var previous=Current.Value??throw new InvalidOperationException("TRACKED_OPERATION_REQUIRED_FOR_COMMIT");
        lock(Sync)
        {
            previous.Token.ThrowIfCancellationRequested();
            if(closing&&!previous.ClosingWork&&previous.CommitDepth==0)
                throw new OperationCanceledException("SHUTDOWN_BEFORE_COMMIT",normal.Token);
            // Admission to an existing commit is atomic with BeginClosing. Once admitted,
            // never cancel/abandon a frozen synchronous mutation halfway through it.
            Current.Value=previous with {Token=CancellationToken.None,CommitDepth=previous.CommitDepth+1};
        }
        return new RestoreFrame(previous);
    }
    public static Task RunAsync(string name,string engine,Func<Task> action)=>Start(name,engine,action,false);
    public static Task RunClosingAsync(string name,string engine,Func<Task> action)=>Start(name,engine,action,true);
    static Task Start(string name,string engine,Func<Task> action,bool closingWork)
    {
        ArgumentNullException.ThrowIfNull(action);
        Entry entry;Frame frame;
        lock(Sync)
        {
            var parent=Current.Value;
            if(closingWork&&!closing)throw new InvalidOperationException("CLOSING_WORK_REQUIRES_QUIESCE");
            var permitted=closingWork||parent is {ClosingWork:true}||parent is {CommitDepth:>0};
            if(closing&&!permitted)throw new OperationCanceledException("SHUTDOWN_REJECTS_NEW_WORK",normal.Token);
            var token=permitted?CancellationToken.None:parent?.Token??normal.Token;
            token.ThrowIfCancellationRequested();
            frame=new(token,closingWork||parent is {ClosingWork:true},parent?.CommitDepth??0);
            entry=new(++serial,new(TaskCreationOptions.RunContinuationsAsynchronously));
            // Register completion before executing any part of the delegate, including
            // its synchronous prefix. This closes the exit/continuation accounting gap.
            Work.Add(entry.Id,entry);
        }
        _=Execute(entry,frame,name,engine,action);
        return entry.Completion.Task;
    }
    static async Task Execute(Entry entry,Frame frame,string name,string engine,Func<Task> action)
    {
        var previous=Current.Value;Current.Value=frame;
        OwnedLaunchBoundary.Operation? audit=null;Exception? failure=null;
        try
        {
            frame.Token.ThrowIfCancellationRequested();
            audit=OwnedLaunchBoundary.BeginOperation(name,engine);
            await action().ConfigureAwait(false);
            audit.Success();
        }
        catch(Exception error){failure=error;audit?.Error(error);}
        finally
        {
            try{audit?.Dispose();}catch(Exception error){failure=failure==null?error:new AggregateException(failure,error);}
            Current.Value=previous;
            lock(Sync)
            {
                Work.Remove(entry.Id);
                if(failure!=null&&failure is not OperationCanceledException)
                    ObservedErrors.Add(failure.GetType().Name+":"+failure.Message);
            }
            if(failure is OperationCanceledException cancelled)
                entry.Completion.TrySetCanceled(cancelled.CancellationToken.IsCancellationRequested?cancelled.CancellationToken:new CancellationToken(true));
            else if(failure!=null)entry.Completion.TrySetException(failure);
            else entry.Completion.TrySetResult();
        }
    }
    public static void BeginClosing()
    {
        CancellationTokenSource? cancel=null;
        lock(Sync){if(!closing){closing=true;cancel=normal;}}
        // Cancellation callbacks may complete work; do not execute them inside Sync.
        cancel?.Cancel();
    }
    public static async Task<ShutdownDrainResult> DrainAsync(TimeSpan timeout)
    {
        if(timeout<=TimeSpan.Zero||timeout>TimeSpan.FromMinutes(10))throw new ArgumentOutOfRangeException(nameof(timeout));
        if(!IsClosing)throw new InvalidOperationException("DRAIN_REQUIRES_QUIESCE");
        var elapsed=Stopwatch.StartNew();
        while(true)
        {
            Entry[] entries;string[] errors;
            lock(Sync){entries=Work.Values.ToArray();errors=ObservedErrors.ToArray();}
            var boundary=OwnedLaunchBoundary.Activity();
            if(entries.Length==0&&boundary.OwnedChildren==0)
                return new(boundary.Healthy&&boundary.OpenOperations==0&&boundary.PendingAttempts==0,
                    0,0,errors.Concat(boundary.Faults).ToArray(),boundary.OpenOperations,boundary.PendingAttempts);
            var remaining=timeout-elapsed.Elapsed;
            if(remaining<=TimeSpan.Zero)
                return new(false,entries.Length,boundary.OwnedChildren,errors.Concat(boundary.Faults).Append("DRAIN_TIMEOUT_RETAINS_LIVE_OWNER").ToArray(),boundary.OpenOperations,boundary.PendingAttempts);
            // A completed process is not a completed operation: keep awaiting tracked
            // continuations, final durable receipts and non-cancelable commits.
            var delay=Task.Delay(remaining<TimeSpan.FromMilliseconds(40)?remaining:TimeSpan.FromMilliseconds(40));
            if(entries.Length>0)
            {
                var all=Task.WhenAll(entries.Select(x=>x.Completion.Task));
                await Task.WhenAny(all,delay).ConfigureAwait(false);
                if(all.IsFaulted)_=all.Exception; // outcome remains on each tracked task; observe aggregate too.
            }
            else await delay.ConfigureAwait(false);
        }
    }
    public static bool ResumeAfterAbortedClose()
    {
        // Read the boundary outside Sync (the seal reads the task counter under its own
        // lock), avoiding an inverted lock order while a callback completes.
        var boundary=OwnedLaunchBoundary.Activity();
        lock(Sync)
        {
            if(!closing||Work.Count!=0||boundary.OwnedChildren!=0||boundary.OpenOperations!=0||boundary.PendingAttempts!=0||!boundary.Healthy||boundary.Sealed)return false;
            var previous=normal;normal=new();closing=false;previous.Dispose();return true;
        }
    }
}
