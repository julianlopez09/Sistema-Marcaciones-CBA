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
        dialog.RequestedTheme=root.RequestedTheme;
        dialog.PrimaryButtonStyle=(Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["InstitutionalPrimaryButton"];
        activeDialog=dialog;
        try{return await dialog.ShowAsync();}
        finally{if(ReferenceEquals(activeDialog,dialog))activeDialog=null;}
    }
}
