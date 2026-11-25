using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using SistemaMarcaciones.F13.Integrated.Native;
using System.Diagnostics;

namespace SistemaMarcaciones.Desktop.Pilot.WinUI;

// UI-thread task set. A task is registered before callers can return to the message pump.
// This never blocks the dispatcher and never owns or terminates an unrelated process.
internal sealed class UiWorkSet
{
    readonly HashSet<Task> pending=[];
    public Task Track(Task task)
    {
        if(task.IsCompleted)return task;
        pending.Add(task);_=Remove(task);return task;
    }
    async Task Remove(Task task){try{await task;}catch{ /* The original caller retains the exception. */ }finally{pending.Remove(task);}}
    public async Task<bool> Drain(TimeSpan timeout)
    {
        var elapsed=Stopwatch.StartNew();
        while(true)
        {
            var active=pending.Where(t=>!t.IsCompleted).ToArray();if(active.Length==0)return true;
            var remaining=timeout-elapsed.Elapsed;if(remaining<=TimeSpan.Zero)return false;
            var all=Task.WhenAll(active);
            if(await Task.WhenAny(all,Task.Delay(remaining))!=all)return false;
            try{await all;}catch{ /* Safe/caller already reports the actual operation failure. */ }
        }
    }
}

internal sealed partial class IntegratedWindow
{
    readonly UiWorkSet hostUi=new();
    bool hostInteractionWasEnabled;
    bool closePermitted;
    Task? shutdownTask;
    bool AcceptsUiWork=>!hostClosing&&!OperationLifetime.IsClosing;
    Task HostWork(string label,Func<Task> action)=>AcceptsUiWork
        ?OperationLifetime.RunAsync(label,"CSHARP_NATIVE:"+selectedPopulation,action):Task.CompletedTask;
    void HostClosing(AppWindow sender,AppWindowClosingEventArgs args)
    {
        if(closePermitted)return;
        // Cancel synchronously, before the first await. Repeated X/Alt-F4 cannot bypass draining.
        args.Cancel=true;
        _=RequestSafeCloseAsync();
    }
    // Window.Close() does not raise AppWindow.Closing. Programmatic callers must use
    // this same preflight/drain/seal path; only its final permitted Close() bypasses it.
    internal async Task<bool> RequestSafeCloseAsync()
    {
        if(closePermitted)return true;
        try
        {
            if(shutdownTask is not {IsCompleted:false})
            {
                if(HasCenterDraft())
                {state.Text="Cierre no iniciado: guarda o revierte el borrador del Centro. No se ha descartado.";return false;}
                if(editors.Values.Any(e=>e.BlocksHostClose))
                {state.Text="Cierre no iniciado: resuelve el diálogo o conflicto abierto del editor. Borrador conservado.";return false;}
                shutdownTask=CloseHostAsync();
            }
            var requested=shutdownTask??throw new InvalidOperationException("SAFE_CLOSE_TASK_NOT_CREATED");
            await requested;
            return closePermitted;
        }
        catch(Exception error)
        {
            PilotLog.Event("SR2_SAFE_CLOSE_REQUEST_NOT_CONFIRMED",error.ToString());
            return false;
        }
    }
    async Task CloseHostAsync()
    {
        hostClosing=true;hostInteractionWasEnabled=hostInteractionGate.IsEnabled;hostInteractionGate.IsEnabled=false;state.Text="Cerrando: cancelando admisiones y esperando operaciones propias…";
        foreach(var editor in editors.Values.ToArray())editor.FreezeForHostClose();
        try
        {
            // Stop admission synchronously with Closing, before any queued continuation can commit.
            OperationLifetime.BeginClosing();
            // Even an already-idle session must leave the native Closing callback before Close().
            await Task.Yield();
            var drained=await OperationLifetime.DrainAsync(TimeSpan.FromSeconds(30));
            if(!drained.Complete)throw new InvalidOperationException("OWN_TASK_OR_CHILD_DRAIN_PENDING_NO_CLOSE");
            if(!await hostUi.Drain(TimeSpan.FromSeconds(30)))throw new InvalidOperationException("HOST_UI_CONTINUATION_PENDING_NO_CLOSE");
            var closingEditors=editors.Values.ToArray();
            foreach(var editor in closingEditors)await editor.PrepareForHostCloseAsync();
            drained=await OperationLifetime.DrainAsync(TimeSpan.FromSeconds(30));
            if(!drained.Complete)throw new InvalidOperationException("CLOSE_SAVE_DRAIN_PENDING_NO_CLOSE");
            foreach(var editor in closingEditors)editor.CommitHostClose();
            var editorExit=Task.WhenAll(closingEditors.Select(e=>e.ActualClose));
            if(await Task.WhenAny(editorExit,Task.Delay(TimeSpan.FromSeconds(15)))!=editorExit)
                throw new InvalidOperationException("EDITOR_CLOSE_NOT_OBSERVED_NO_SESSION_SEAL");
            await editorExit;
            drained=await OperationLifetime.DrainAsync(TimeSpan.FromSeconds(30));
            if(!drained.Complete)throw new InvalidOperationException("FINAL_OWN_DRAIN_PENDING_NO_SESSION_SEAL");
            if(!await hostUi.Drain(TimeSpan.FromSeconds(30)))throw new InvalidOperationException("FINAL_HOST_UI_CONTINUATION_PENDING_NO_SESSION_SEAL");
            // The boundary independently rejects open operations/children. Closed never seals it.
            OwnedLaunchBoundary.CloseSession();
            PilotLog.Event("SR1_QUIESCENT_SESSION_SEALED_BEFORE_WINDOW_CLOSE");
            closePermitted=true;Close();
        }
        catch(Exception error)
        {
            PilotLog.Event("SR1_SHUTDOWN_NOT_CONFIRMED",error.ToString());
            // A timed-out child is not killed here, and an incomplete session is never certified.
            var drained=await OperationLifetime.DrainAsync(TimeSpan.FromSeconds(1));
            var uiDrained=await hostUi.Drain(TimeSpan.FromSeconds(1));
            if(drained.Complete&&uiDrained&&OperationLifetime.ResumeAfterAbortedClose())
            {
                hostClosing=false;hostInteractionGate.IsEnabled=hostInteractionWasEnabled;
                foreach(var editor in editors.Values.ToArray())editor.ResumeAfterHostCloseAborted();
                state.Text="Cierre no confirmado; ventana y borradores conservados: "+error.Message;
                _=Render();
            }
            else state.Text="Cierre pendiente; no se ha forzado la salida ni sellado como completa: "+error.Message;
        }
    }
}

public sealed partial class DetachedExcelWindow
{
    readonly UiWorkSet uiWork=new();
    readonly TaskCompletionSource<bool> actualClose=new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool closeRequested,closePermitted,hostClosePreparation;
    bool interactionGateFrozen,interactionWasEnabled;
    Task? editorCloseTask;
    ContentDialog? activeDialog;
    internal bool BlocksHostClose=>activeDialog!=null||manualDialog||buffer.Conflict;
    internal Task ActualClose=>actualClose.Task;
    bool AcceptsEditorWork=>!closed&&!closeRequested&&!OperationLifetime.IsClosing;
    void EditorClosing(AppWindow sender,AppWindowClosingEventArgs args)
    {
        if(closePermitted)return;
        args.Cancel=true;
        if(hostClosePreparation||editorCloseTask is {IsCompleted:false})return;
        if(BlocksHostClose){state.Text="Cierra o resuelve el diálogo/conflicto antes de salir. Borrador conservado.";return;}
        editorCloseTask=CloseEditorAsync();
    }
    void FreezeEditor()
    {
        closeRequested=true;watcher.Stop();activeOptions?.Hide();
        if(!interactionGateFrozen){interactionWasEnabled=editorInteractionGate.IsEnabled;interactionGateFrozen=true;}
        editorInteractionGate.IsEnabled=false;
    }
    void ThawEditor()
    {
        if(interactionGateFrozen){editorInteractionGate.IsEnabled=interactionWasEnabled;interactionGateFrozen=false;}
    }
    internal void FreezeForHostClose(){if(closed)return;hostClosePreparation=true;FreezeEditor();}
    internal void ResumeAfterHostCloseAborted()
    {
        if(closed)return;
        hostClosePreparation=false;closeRequested=false;ThawEditor();watcher.Start();
        if(grid!=null&&buffer.Pending.Length==0)Select(grid.Coordinate,false);
    }
    async Task PrepareCloseSave(bool host)
    {
        if(!await uiWork.Drain(TimeSpan.FromSeconds(30)))throw new InvalidOperationException("EDITOR_WORK_STILL_ACTIVE_NO_CLOSE");
        if(BlocksHostClose)throw new InvalidOperationException("EDITOR_DIALOG_OR_CONFLICT_PRESERVED_NO_CLOSE");
        async Task SaveAndCheck()
        {
            await Save();
            if(buffer.Pending.Length!=0||buffer.Conflict)throw new InvalidOperationException("EDITOR_DRAFT_NOT_SAVED_NO_CLOSE");
        }
        // Unlike Safe, this propagates save failure to the close coordinator.
        if(host)await OperationLifetime.RunClosingAsync("EDITOR_SHUTDOWN_SAVE","CSHARP_NATIVE_DETACHED_REVIEW",SaveAndCheck);
        else await OperationLifetime.RunAsync("EDITOR_CLOSE_SAVE","CSHARP_NATIVE_DETACHED_REVIEW",SaveAndCheck);
    }
    internal Task PrepareForHostCloseAsync()=>closed?Task.CompletedTask:PrepareCloseSave(true);
    internal void CommitHostClose(){if(closed)return;closePermitted=true;Close();}
    async Task CloseEditorAsync()
    {
        FreezeEditor();
        await Task.Yield();
        try{await PrepareCloseSave(false);closePermitted=true;Close();}
        catch(Exception error)
        {
            PilotLog.Event("SR1_EDITOR_CLOSE_ABORTED_DRAFT_PRESERVED",error.ToString());
            if(!hostClosePreparation){closeRequested=false;ThawEditor();watcher.Start();}
            state.Text="No se cerró; borrador conservado: "+error.Message;
        }
    }
    async Task<ContentDialogResult> ShowTrackedDialog(ContentDialog dialog)
    {
        if(!AcceptsEditorWork)return ContentDialogResult.None;
        activeDialog=dialog;
        try{return await dialog.ShowAsync();}
        finally{if(ReferenceEquals(activeDialog,dialog))activeDialog=null;}
    }
}
