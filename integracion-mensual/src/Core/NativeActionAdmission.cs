using System.Diagnostics;
using SistemaMarcaciones.Monthly.Host.NativeIntegration;

namespace SistemaMarcaciones.F13.Integrated.Native;

public sealed record NativeActionGateConfiguration(B09File PowerShell,B09File Script);
/// <summary>Refreshes an expired admission through the exact sealed tooling port, never by rewriting a receipt.</summary>
public static class NativeActionAdmission
{
    static readonly SemaphoreSlim serial=new(1,1);
    static B09File? latest;
    public static async Task<B09File> FromEnvironment(CancellationToken ct=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,OperationLifetime.CurrentToken);
        ct=linked.Token;OperationLifetime.Checkpoint();
        await serial.WaitAsync(ct);try
        {
            var path=Environment.GetEnvironmentVariable("CBA_F13_B09_FRESH_MEDIUM")??throw new InvalidDataException("INITIAL_NATIVE_ADMISSION_REQUIRED");
            var config=Environment.GetEnvironmentVariable("CBA_F13_ACTION_GATE_CONFIG")??throw new InvalidDataException("SEALED_ACTION_GATE_CONFIGURATION_REQUIRED");
            var sha=Environment.GetEnvironmentVariable("CBA_F13_ACTION_GATE_CONFIG_SHA256")??throw new InvalidDataException("SEALED_ACTION_GATE_CONFIGURATION_PIN_REQUIRED");
            latest=await Fresh(latest??CompletionExports.Pin(path),new(config,sha),ct);ct.ThrowIfCancellationRequested();OperationLifetime.Checkpoint();return latest;
        }finally{serial.Release();}
    }
    public static async Task<B09File> Fresh(B09File current,B09File configuration,CancellationToken ct=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,OperationLifetime.CurrentToken);
        ct=linked.Token;ct.ThrowIfCancellationRequested();OperationLifetime.Checkpoint();
        try { B09Contract.Medium(current);ct.ThrowIfCancellationRequested();OperationLifetime.Checkpoint();return current; }
        catch(InvalidDataException error) when(error.Message=="B09_FRESH_MEDIUM_REQUIRED") { }
        var gate=configuration.Read<NativeActionGateConfiguration>();
        CompletionExports.Verify(gate.PowerShell);CompletionExports.Verify(gate.Script);
        var root=Path.GetDirectoryName(Path.GetFullPath(gate.Script.Path))!;
        var attempt=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var info=new ProcessStartInfo(gate.PowerShell.Path){WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true};
        foreach(var arg in new[]{"-NoLogo","-NoProfile","-NonInteractive","-File",gate.Script.Path,"-Target","MEDIUM","-Attempt",attempt})info.ArgumentList.Add(arg);
        foreach(var key in info.Environment.Keys.Where(k=>k.StartsWith("CBA_",StringComparison.OrdinalIgnoreCase)||k.Contains("SQL_CONNECTION",StringComparison.OrdinalIgnoreCase)).ToArray())info.Environment.Remove(key);
        var started=DateTimeOffset.UtcNow;
        ct.ThrowIfCancellationRequested();OperationLifetime.Checkpoint();
        await using var launch=OwnedLaunchBoundary.StartVerified(info,"RESOURCE_GATE_MEDIUM");
        // The lease alone owns the Process handle; disposing it before the lease hid
        // exit state on exception paths in the old integration.
        var process=launch.Process;process.StandardInput.Close();
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try {await process.WaitForExitAsync(ct);launch.Exited();}
        catch(Exception original)
        {
            Exception? cleanup=null;
            try{await launch.StopAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);}
            catch(Exception error){cleanup=error;}
            try{await OwnedLaunchBoundary.ObserveOutputsAsync(stdout,stderr,TimeSpan.FromSeconds(8)).ConfigureAwait(false);}
            catch(Exception error){cleanup=cleanup==null?error:new AggregateException(cleanup,error);}
            if(cleanup!=null)throw new AggregateException(original,cleanup);
            throw;
        }
        var outputs=await OwnedLaunchBoundary.ObserveOutputsAsync(stdout,stderr,TimeSpan.FromSeconds(8));
        var output=outputs[0];var errorText=outputs[1];
        NativeAttendanceCoordinator.WriteExact(Path.Combine(root,"NATIVE_ACTION_GATE_"+attempt+"_"+process.Id+".json"),new{
            Contract="F13_OWNED_NATIVE_ACTION_ADMISSION_V1",Started=started,Ended=DateTimeOffset.UtcNow,gate.PowerShell,gate.Script,
            ProcessId=process.Id,process.ExitCode,Stdout=output,Stderr=errorText,Scope="RESOURCE_GATE_TOOLING_ONLY_NOT_LABOUR_CONSUMER",NoPythonFallback=true,NoDebugger=true});
        if(process.ExitCode!=0)throw new InvalidOperationException("FRESH_NATIVE_ACTION_ADMISSION_REJECTED");
        // Even after an observed child EXIT, a close can win before the UI continuation.
        // A valid resource receipt never authorizes a new motor/commit after quiesce.
        ct.ThrowIfCancellationRequested();OperationLifetime.Checkpoint();
        var path=output.Split('\n').Select(l=>l.Trim()).Single(l=>l.StartsWith("F13_MEDIUM_RECEIPT=",StringComparison.Ordinal))["F13_MEDIUM_RECEIPT=".Length..];
        if(Path.GetDirectoryName(Path.GetFullPath(path))!=root)throw new InvalidDataException("NATIVE_ACTION_GATE_RECEIPT_OUTSIDE_SEALED_TOOL_SCOPE");
        var pin=CompletionExports.Pin(path);B09Contract.Medium(pin);ct.ThrowIfCancellationRequested();OperationLifetime.Checkpoint();return pin;
    }
}
