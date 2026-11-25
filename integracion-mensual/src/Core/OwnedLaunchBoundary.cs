using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SistemaMarcaciones.F13.Integrated.Native;

public sealed record LaunchRule(string Executable,string SHA256,string Purpose,string[] Arguments,
    bool VariableFinalNumericArgument=false,string? Script=null,string? ScriptSHA256=null);
public sealed record LaunchPolicy(string Contract,string Identity,LaunchRule[] Rules);
public sealed record BoundaryVerification(bool Complete,int Events,int Attempts,int Started,int Denied,
    int Exited,int Errors,int OpenOperations,string[] Gaps,bool UniversalOSAbsence=false);
public sealed record BoundaryActivity(int OpenOperations,int PendingAttempts,int OwnedChildren,bool Healthy,bool Sealed,string[] Faults);

/// <summary>Additive observation and fail-closed exact allowlist of this explicit product port.
/// It neither hooks OS calls nor changes any frozen consumer, default, rule or receipt contract.</summary>
public static class OwnedLaunchBoundary
{
    public const string Contract="F13_OWNED_PRODUCT_LAUNCH_BOUNDARY_V1";
    static readonly object Serial=new();
    static readonly AsyncLocal<Operation?> Current=new();
    static readonly HashSet<string> OpenOperations=new(StringComparer.Ordinal);
    sealed class AttemptState {public bool Terminal;}
    static readonly Dictionary<string,AttemptState> Attempts=new(StringComparer.Ordinal);
    static readonly Dictionary<string,LaunchLease> Children=new(StringComparer.Ordinal);
    static readonly List<string> Faults=new();
    static Func<int>? taskCount;
    static string? directory,session,previous;
    static LaunchPolicy? policy;
    static long sequence;
    static bool closed;
    static bool sealing;
    internal static void BindTaskCount(Func<int> count){lock(Serial)taskCount=count;}
    public static BoundaryActivity Activity()
    {lock(Serial)return new(OpenOperations.Count,Attempts.Values.Count(a=>!a.Terminal),Children.Count,Faults.Count==0,closed,Faults.ToArray());}
    public static async Task<string[]> ObserveOutputsAsync(Task<string> stdout,Task<string> stderr,TimeSpan timeout)
    {
        if(timeout<=TimeSpan.Zero||timeout>TimeSpan.FromMinutes(1))throw new ArgumentOutOfRangeException(nameof(timeout));
        var both=Task.WhenAll(stdout,stderr);
        try{return await both.WaitAsync(timeout).ConfigureAwait(false);}
        catch(Exception error)
        {
            if(both.IsFaulted)_=both.Exception;
            if(!both.IsCompleted)
                _=both.ContinueWith(t=>{_=t.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            // A bounded timeout is not a claim that pending readers finished. Retain
            // a sticky coverage fault, observe any later fault, and refuse the seal.
            lock(Serial)Faults.Add("OWNED_PIPE_DRAIN_FAILED:"+error.GetType().Name);
            throw;
        }
    }
    public static string SessionDirectory=>directory??throw new InvalidOperationException("LAUNCH_AUDIT_NOT_INITIALIZED");
    static string Hash(byte[] data)=>Convert.ToHexString(SHA256.HashData(data));
    static string FileHash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
    static string Label(string value)
    {if(string.IsNullOrWhiteSpace(value)||value.Length>200||value.Any(char.IsControl))throw new InvalidDataException("AUDIT_LABEL_INVALID");return value;}
    static void NoLinks(string path)
    {
        for(var cursor=Path.GetFullPath(path);cursor!=null;cursor=Path.GetDirectoryName(cursor))
        {FileSystemInfo item=Directory.Exists(cursor)?new DirectoryInfo(cursor):new FileInfo(cursor);if(item.Exists&&item.LinkTarget!=null)throw new InvalidDataException("AUDIT_REAL_LINK_REJECTED");}
    }
    public static string Initialize(string root,string policyFile,string policySHA256)
    {
        return InitializeCore(root,policyFile,policySHA256,false);
    }
    public static string InitializeLocal(string root,string policyFile,string policySHA256)
    {
        var localRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CBA","MarcacionesWinUI");
        var full=Path.GetFullPath(root);
        if(!full.StartsWith(localRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EXPLICIT_PARALLEL_LOCAL_PRODUCT_ROOT_REQUIRED");
        return InitializeCore(full,policyFile,policySHA256,true);
    }
    static string InitializeCore(string root,string policyFile,string policySHA256,bool localProduct)
    {
        lock(Serial)
        {
            if(directory!=null)throw new InvalidOperationException("AUDIT_ALREADY_INITIALIZED");
            root=Path.GetFullPath(root);NoLinks(root);NoLinks(policyFile);
            if(!localProduct&&!root.Split(Path.DirectorySeparatorChar).Contains("TEST_FIXTURE_ONLY",StringComparer.Ordinal))throw new InvalidDataException("AUDIT_SANDBOX_REQUIRED");
            if(FileHash(policyFile)!=policySHA256)throw new InvalidDataException("LAUNCH_POLICY_PIN_CHANGED");
            var p=JsonSerializer.Deserialize<LaunchPolicy>(File.ReadAllBytes(policyFile))??throw new InvalidDataException("LAUNCH_POLICY_MISSING");
            if(p.Contract!="F13_EXACT_OWN_LAUNCH_ALLOWLIST_V1"||(!localProduct&&p.Rules.Length==0)||p.Rules.Any(r=>!Path.IsPathFullyQualified(r.Executable)||r.SHA256.Length!=64||r.Purpose.Length==0))throw new InvalidDataException("EXACT_LAUNCH_POLICY_REQUIRED");
            session=Guid.NewGuid().ToString("N");directory=Path.Combine(root,session);Directory.CreateDirectory(directory);
            policy=p;sequence=0;previous=new string('0',64);closed=false;sealing=false;
            Record("SESSION_BEGIN",new{p.Identity,PolicyPath=Path.GetFullPath(policyFile),PolicySHA256=policySHA256,
                ComponentPath=typeof(OwnedLaunchBoundary).Assembly.Location,ComponentSHA256=FileHash(typeof(OwnedLaunchBoundary).Assembly.Location),
                Coverage="OWN_EXPLICIT_PRODUCT_LAUNCH_PORT_ONLY",LocalParallelProduct=localProduct,UniversalOSAbsence=false});
            return directory;
        }
    }
    public static void InitializeFromEnvironment()=>Initialize(
        Environment.GetEnvironmentVariable("CBA_F13_LAUNCH_AUDIT_ROOT")??throw new InvalidDataException("EXPLICIT_LAUNCH_AUDIT_ROOT_REQUIRED"),
        Environment.GetEnvironmentVariable("CBA_F13_LAUNCH_POLICY")??throw new InvalidDataException("EXPLICIT_LAUNCH_POLICY_REQUIRED"),
        Environment.GetEnvironmentVariable("CBA_F13_LAUNCH_POLICY_SHA256")??throw new InvalidDataException("EXPLICIT_LAUNCH_POLICY_PIN_REQUIRED"));
    static void Record(string kind,object details)
    {
        lock(Serial)
        {
            if(directory==null||closed)throw new InvalidOperationException("AUDIT_CLOSED_OR_UNINITIALIZED");
            var op=Current.Value;
            var body=JsonSerializer.SerializeToUtf8Bytes(new{Contract,SessionId=session,Sequence=sequence,Kind=kind,
                AtUTC=DateTimeOffset.UtcNow,MonotonicTicks=Stopwatch.GetTimestamp(),ParentPID=Environment.ProcessId,
                OperationId=op?.Id,Operation=op?.Name,Engine=op?.Engine,PreviousEventSHA256=previous,Details=details});
            var path=Path.Combine(directory,sequence.ToString("D8")+"_"+kind+".json");
            try
            {
                NoLinks(path);
                using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){stream.Write(body);stream.Flush(true);}
                previous=Hash(body);sequence++;
            }
            catch(Exception e){Faults.Add("AUDIT_WRITE_FAILED:"+kind+":"+e.GetType().Name);throw;}
        }
    }
    public sealed class Operation:IDisposable
    {
        internal readonly string Id=Guid.NewGuid().ToString("N");
        internal readonly string Name,Engine;
        readonly Operation? parent;string result="INCOMPLETE";bool disposed;
        internal Operation(string name,string engine){Name=Label(name);Engine=Label(engine);parent=Current.Value;
            lock(Serial){if(sealing||closed||Faults.Count!=0)throw new InvalidOperationException("AUDIT_NOT_ACCEPTING_NEW_OPERATIONS");Current.Value=this;try{Record("OP_BEGIN",new{Id,Name,Engine});OpenOperations.Add(Id);}catch{Current.Value=parent;throw;}}}
        public void Success()=>result="COMPLETED";
        public void Error(Exception e)=>result=e is OperationCanceledException?"CANCELLED":"ERROR";
        public void Dispose(){if(disposed)return;lock(Serial){Record("OP_END",new{Id,Result=result});OpenOperations.Remove(Id);disposed=true;}Current.Value=parent;}
    }
    public static Operation BeginOperation(string name,string engine)=>new(name,engine);
    public sealed class LaunchLease:IDisposable,IAsyncDisposable
    {
        public Process Process {get;}
        readonly string attempt;bool terminal,disposed,exitObserved;
        readonly SemaphoreSlim cleanup=new(1,1);
        internal LaunchLease(Process process,string id){Process=process;attempt=id;}
        public bool ExitObserved=>exitObserved;
        public bool HandleDisposed=>disposed;
        void Terminal(string kind)
        {
            lock(Serial)
            {
                if(terminal)return;
                if(disposed&&exitObserved)throw new InvalidOperationException("TERMINAL_AUDIT_FAILED_HANDLE_ALREADY_DISPOSED");
                if(!Process.HasExited)throw new InvalidOperationException("OWNED_CHILD_EXIT_NOT_OBSERVED");
                exitObserved=true;
                try
                {
                    if(kind=="EXIT")Record(kind,new{AttemptId=attempt,ChildPID=Process.Id,Process.ExitCode});
                    else Record(kind,new{AttemptId=attempt,ChildPID=Process.Id,HasExited=true,Process.ExitCode});
                    Attempts[attempt].Terminal=true;terminal=true;
                }
                finally
                {
                    Children.Remove(attempt);
                    // Exit was genuinely observed. An audit-write failure must retain
                    // its pending/fault state, but must not leak the already-dead handle
                    // or retry a terminal record by dereferencing a disposed Process.
                    if(!terminal){Process.Dispose();disposed=true;}
                }
            }
        }
        public void Exited()=>Terminal("EXIT");
        public void Cancelled()=>Terminal("CANCELLED");
        public async Task StopAsync(TimeSpan timeout)
        {
            if(timeout<=TimeSpan.Zero||timeout>TimeSpan.FromMinutes(1))throw new ArgumentOutOfRangeException(nameof(timeout));
            using var bound=new CancellationTokenSource(timeout);
            await cleanup.WaitAsync(bound.Token).ConfigureAwait(false);
            try
            {
                if(terminal||(disposed&&exitObserved))return;
                // This exact retained Process handle was created by StartVerified. Never
                // rediscover/kill a PID or unrelated process, and never kill a whole tree.
                if(!Process.HasExited)Process.Kill();
                await Process.WaitForExitAsync(bound.Token).ConfigureAwait(false);
                Cancelled();
            }
            catch(Exception e){lock(Serial)Faults.Add("OWNED_CHILD_CLEANUP_FAILED:"+e.GetType().Name);throw;}
            finally{cleanup.Release();}
        }
        public async ValueTask DisposeAsync()
        {
            if(disposed)return;
            if(!terminal){if(Process.HasExited)Exited();else await StopAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);}
            if(terminal){Process.Dispose();disposed=true;}
        }
        public void Dispose()=>DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    public static LaunchLease StartVerified(ProcessStartInfo info,string purpose)
    {
        if(Current.Value==null)throw new InvalidOperationException("AUDITED_OPERATION_REQUIRED_BEFORE_LAUNCH");
        var id=Guid.NewGuid().ToString("N");var argv=info.ArgumentList.ToArray();var argHash=Hash(JsonSerializer.SerializeToUtf8Bytes(new{Arguments=info.Arguments,ArgumentList=argv}));
        // No supplied arguments are written to disk before their complete allowlist check.
        lock(Serial)
        {
            if(sealing||closed||Faults.Count!=0)throw new InvalidOperationException("AUDIT_NOT_ACCEPTING_NEW_LAUNCHES");
            Record("ATTEMPT",new{AttemptId=id,Purpose=Label(purpose),Executable=info.FileName,ArgumentsSHA256=argHash,ArgumentCount=argv.Length,BeforeProcessStart=true});
            Attempts.Add(id,new());
        }
        LaunchRule? rule=null;
        try
        {
            if(info.UseShellExecute||!Path.IsPathFullyQualified(info.FileName)||!string.IsNullOrEmpty(info.Arguments))throw new InvalidDataException("SHELL_OR_RELATIVE_LAUNCH_DENIED");
            NoLinks(info.FileName);
            rule=policy!.Rules.SingleOrDefault(r=>string.Equals(Path.GetFullPath(r.Executable),Path.GetFullPath(info.FileName),StringComparison.OrdinalIgnoreCase)&&r.Purpose==purpose)
                ??throw new InvalidDataException("EXECUTABLE_OR_PURPOSE_NOT_ALLOWLISTED");
            if(FileHash(info.FileName)!=rule.SHA256)throw new InvalidDataException("EXECUTABLE_PIN_CHANGED");
            if(argv.Length!=rule.Arguments.Length||!argv.Select((a,i)=>rule.VariableFinalNumericArgument&&i==argv.Length-1?
                a.Length is >0 and <24&&a.All(char.IsAsciiDigit):a==rule.Arguments[i]).All(x=>x))throw new InvalidDataException("ARGUMENT_SHAPE_NOT_ALLOWLISTED");
            if(rule.Script!=null){NoLinks(rule.Script);if(FileHash(rule.Script)!=rule.ScriptSHA256)throw new InvalidDataException("SCRIPT_PIN_CHANGED");}
            Record("ALLOW",new{AttemptId=id,rule.SHA256,AllowedArguments=argv,rule.Script,rule.ScriptSHA256});
        }
        catch(Exception e){lock(Serial){Record("DENIED",new{AttemptId=id,Reason=e.Message,ArgumentsRedacted=true});Attempts[id].Terminal=true;}throw;}
        Process? process=null;LaunchLease? lease=null;
        try
        {
            process=Process.Start(info)??throw new InvalidOperationException("OWN_PROCESS_NOT_STARTED");
            lease=new(process,id);lock(Serial)Children.Add(id,lease);
            Record("STARTED",new{AttemptId=id,ChildPID=process.Id,ExecutableSHA256=rule.SHA256});
            return lease;
        }
        catch(Exception e)
        {
            // A launch that cannot be durably recorded must not silently continue.
            if(process!=null)
            {
                try{if(!process.HasExited)process.Kill();if(!process.WaitForExit(8000))throw new TimeoutException("START_FAILURE_CHILD_EXIT_TIMEOUT");}
                catch(Exception cleanupError){lock(Serial)Faults.Add("START_FAILURE_CLEANUP_FAILED:"+cleanupError.GetType().Name);throw new AggregateException(e,cleanupError);}
            }
            lock(Serial)
            {
                try{Record("START_FAILED",new{AttemptId=id,ErrorType=e.GetType().Name,UncertainStart=process!=null,ChildPID=process?.Id,ChildExitObserved=process?.HasExited??true});Attempts[id].Terminal=true;}
                finally{if(process?.HasExited??true){Children.Remove(id);process?.Dispose();}}
            }
            throw;
        }
    }
    public static void CloseSession()
    {
        lock(Serial)
        {
            if(closed)return;
            var active=taskCount?.Invoke()??0;
            if(Faults.Count!=0||OpenOperations.Count!=0||Attempts.Values.Any(a=>!a.Terminal)||Children.Count!=0||active!=0)
                throw new InvalidOperationException("AUDIT_SHUTDOWN_NOT_DRAINED_OR_FAULTED");
            sealing=true;
            Record("SESSION_END",new{OpenOperationCount=OpenOperations.Count,Coverage="OWN_EXPLICIT_PRODUCT_LAUNCH_PORT_ONLY",UniversalOSAbsence=false});
            var final=JsonSerializer.SerializeToUtf8Bytes(new{Contract,SessionId=session,Events=sequence,LastEventSHA256=previous,OpenOperations=OpenOperations.ToArray(),PendingAttempts=0,OwnedChildren=0,TrackedTasks=0,Faults=Array.Empty<string>(),ShutdownCompletion="DRAINED_NOT_OS_ABSENCE",UniversalOSAbsence=false});
            try{using(var stream=new FileStream(Path.Combine(SessionDirectory,"SESSION_SEAL.json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)){stream.Write(final);stream.Flush(true);}closed=true;}
            catch(Exception e){Faults.Add("FINAL_SEAL_WRITE_FAILED:"+e.GetType().Name);throw;}
        }
    }
    public static BoundaryVerification Verify(string path)
    {
        var gaps=new List<string>();var files=Directory.GetFiles(path,"*.json").Where(p=>Path.GetFileName(p)!="SESSION_SEAL.json").Order(StringComparer.Ordinal).ToArray();
        var prev=new string('0',64);long seq=0;string? sid=null;int attempts=0,started=0,denied=0,exited=0,errors=0;bool begin=false,end=false;
        var operations=new HashSet<string>(StringComparer.Ordinal);var pending=new HashSet<string>(StringComparer.Ordinal);var running=new HashSet<string>(StringComparer.Ordinal);
        foreach(var file in files)
        {
            try
            {
                var bytes=File.ReadAllBytes(file);using var j=JsonDocument.Parse(bytes);var r=j.RootElement;var kind=r.GetProperty("Kind").GetString()!;var d=r.GetProperty("Details");var currentSid=r.GetProperty("SessionId").GetString();
                sid??=currentSid;if(r.GetProperty("Contract").GetString()!=Contract||currentSid!=sid||r.GetProperty("Sequence").GetInt64()!=seq||r.GetProperty("PreviousEventSHA256").GetString()!=prev)gaps.Add("CHAIN_OR_IDENTITY_GAP");
                if(!Path.GetFileName(file).StartsWith(seq.ToString("D8")+"_",StringComparison.Ordinal))gaps.Add("FILE_SEQUENCE_GAP");
                if(kind=="SESSION_BEGIN")begin=true;if(kind=="SESSION_END"){end=true;if(d.GetProperty("OpenOperationCount").GetInt32()!=0)gaps.Add("SESSION_ENDED_WITH_OPEN_OPERATIONS");}
                if(kind=="OP_BEGIN"&&!operations.Add(d.GetProperty("Id").GetString()!))gaps.Add("DUPLICATE_OPERATION");
                if(kind=="OP_END"){if(!operations.Remove(d.GetProperty("Id").GetString()!))gaps.Add("UNPAIRED_OPERATION_END");if(d.GetProperty("Result").GetString()=="INCOMPLETE")gaps.Add("INCOMPLETE_OPERATION");}
                if(kind=="ATTEMPT"){attempts++;if(!pending.Add(d.GetProperty("AttemptId").GetString()!))gaps.Add("DUPLICATE_ATTEMPT");}
                if(kind=="STARTED"){started++;var id=d.GetProperty("AttemptId").GetString()!;if(!pending.Contains(id)||!running.Add(id))gaps.Add("UNPAIRED_START");}
                if(kind=="DENIED"){denied++;if(!pending.Remove(d.GetProperty("AttemptId").GetString()!))gaps.Add("UNPAIRED_DENIAL");}
                if(kind=="EXIT"){exited++;var id=d.GetProperty("AttemptId").GetString()!;if(!pending.Remove(id)||!running.Remove(id))gaps.Add("UNPAIRED_EXIT");}
                if(kind=="START_FAILED"){errors++;var id=d.GetProperty("AttemptId").GetString()!;pending.Remove(id);running.Remove(id);}
                if(kind=="CANCELLED"){var id=d.GetProperty("AttemptId").GetString()!;pending.Remove(id);running.Remove(id);if(!d.GetProperty("HasExited").GetBoolean())gaps.Add("CANCELLED_PROCESS_EXIT_NOT_PROVEN");}
                if(kind=="LEASE_LEFT_OPEN")gaps.Add("LEASE_LEFT_OPEN");
                prev=Hash(bytes);seq++;
            }
            catch(Exception e){gaps.Add("UNREADABLE_EVENT:"+e.GetType().Name);}
        }
        if(!begin||!end)gaps.Add("SESSION_INTERVAL_NOT_CLOSED");if(operations.Count!=0||pending.Count!=0||running.Count!=0)gaps.Add("OPEN_OPERATIONS_OR_ATTEMPTS");
        var seal=Path.Combine(path,"SESSION_SEAL.json");
        if(!File.Exists(seal))gaps.Add("FINAL_SEAL_MISSING");
        else try
        {
            using var j=JsonDocument.Parse(File.ReadAllBytes(seal));var r=j.RootElement;
            if(r.GetProperty("Contract").GetString()!=Contract||r.GetProperty("SessionId").GetString()!=sid||r.GetProperty("Events").GetInt64()!=seq||r.GetProperty("LastEventSHA256").GetString()!=prev)gaps.Add("FINAL_SEAL_MISMATCH");
            if(r.GetProperty("OpenOperations").GetArrayLength()!=0||r.GetProperty("UniversalOSAbsence").GetBoolean())gaps.Add("FINAL_SEAL_OPEN_OPERATIONS_OR_WRONG_SCOPE");
            foreach(var name in new[]{"PendingAttempts","OwnedChildren","TrackedTasks"})if(r.TryGetProperty(name,out var value)&&value.GetInt32()!=0)gaps.Add("FINAL_SEAL_NOT_DRAINED:"+name);
            if(r.TryGetProperty("Faults",out var faults)&&faults.GetArrayLength()!=0)gaps.Add("FINAL_SEAL_HAS_AUDIT_FAULTS");
        }
        catch(Exception e){gaps.Add("UNREADABLE_FINAL_SEAL:"+e.GetType().Name);}
        return new(gaps.Count==0,files.Length,attempts,started,denied,exited,errors,operations.Count,gaps.ToArray());
    }
}
