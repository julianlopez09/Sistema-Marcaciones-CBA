using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;
using SistemaMarcaciones.Centro.Presentation;
using SistemaMarcaciones.F13.Integrated.Native;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Monthly.Host.NativeIntegration;
using SistemaMarcaciones.Monthly.Management;
using SistemaMarcaciones.Monthly.Native.Outcomes;
using SistemaMarcaciones.F13.Populations;

namespace SistemaMarcaciones.Desktop.Pilot.WinUI;
internal static class IntegratedProgram
{
    internal static bool StartupProbe=>Environment.GetCommandLineArgs().Skip(1).SequenceEqual(new[]{"--ar1-startup-probe"});
    [STAThread] public static void Main()
    {
        OwnedLaunchBoundary.InitializeFromEnvironment();
        var pin=new B09File(Environment.GetEnvironmentVariable("CBA_F13_INTEGRATED_CATALOG")??throw new InvalidDataException("EXPLICIT_INTEGRATED_SANDBOX_CATALOG_REQUIRED"),
            Environment.GetEnvironmentVariable("CBA_F13_INTEGRATED_CATALOG_SHA256")??throw new InvalidDataException("INTEGRATED_CATALOG_PIN_REQUIRED"));
        var catalog=pin.Read<ReleaseCatalog>();if(catalog.Contract!=ReleaseSession.Contract||catalog.Entries.Length==0)throw new InvalidDataException("INTEGRATED_CATALOG_CONTRACT");
        PilotLog.Root=B09Contract.Sandbox(Environment.GetEnvironmentVariable("CBA_F13_B09_LOG_ROOT")??throw new InvalidDataException("SANDBOX_LOG_ROOT_REQUIRED"));
        AppDomain.CurrentDomain.UnhandledException+=(_,e)=>PilotLog.Event("RC05_UNHANDLED_DOMAIN_EXCEPTION",e.ExceptionObject.ToString()??"NO_EXCEPTION_TEXT");
        TaskScheduler.UnobservedTaskException+=(_,e)=>PilotLog.Event("RC05_UNOBSERVED_TASK_EXCEPTION",e.Exception.ToString());
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback=>{SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));_=new IntegratedApp(catalog);});
    }
}
public sealed class IntegratedApp(ReleaseCatalog catalog):Application,IXamlMetadataProvider
{
    readonly IXamlMetadataProvider provider=new XamlControlsXamlMetaDataProvider();Window? window;
    public IXamlType GetXamlType(Type t)=>provider.GetXamlType(t);public IXamlType GetXamlType(string n)=>provider.GetXamlType(n);public XmlnsDefinition[] GetXmlnsDefinitions()=>provider.GetXmlnsDefinitions();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        UnhandledException+=(_,e)=>PilotLog.Event("RC05_UNHANDLED_UI_EXCEPTION",e.Exception+"\nHRESULT="+e.Exception.HResult.ToString("X8")+"\nMessage="+e.Message);
        try
        {
            PilotLog.Event("INTEGRATED_BOOTSTRAP_BEGIN");
            var session=await Task.Run(()=>ReleaseSession.Open(catalog.Entries[0].Configuration));
            Resources.MergedDictionaries.Add(new XamlControlsResources());InstitutionalVisualTokens.Install();
            window=new IntegratedWindow(catalog,session);window.Activate();PilotLog.Event("INTEGRATED_HOST_ACTIVATED",session.Inner.Handle.Id);
            if(IntegratedProgram.StartupProbe)
            {
                await ((IntegratedWindow)window).InitialRender;await Task.Delay(500);
                NativeAttendanceCoordinator.WriteExact(Path.Combine(PilotLog.Root??throw new InvalidDataException("EXPLICIT_PROBE_LOG_ROOT_REQUIRED"),"AR1_STARTUP_PROBE.json"),new{Contract="F13_AR1_OWN_XAML_STARTUP_FOCAL_NOT_VISUAL_QA_V1",Candidate="RC08.1-AR1-QA.5-SR3-RT13",AtUTC=DateTimeOffset.UtcNow,Project=session.Inner.Handle.Id,Revision=session.Inner.Handle.Work.Revision,NativeFacts=NativeAttendanceCoordinator.Collect(session.Inner).Facts.Length,Runtime=new{Version=Environment.Version.ToString(),CoreLib=typeof(object).Assembly.Location,CoreLibSHA256=CompletionExports.Pin(typeof(object).Assembly.Location).SHA256},AllRoutesLoaded=true,NoButtonInvoked=true,NoProcessingRequested=true,NoVisualApproval=true});
                PilotLog.Event("AR1_STARTUP_PROBE_READY_NO_VISUAL_APPROVAL");
                if(!await ((IntegratedWindow)window).RequestSafeCloseAsync())
                    PilotLog.Event("SR2_STARTUP_PROBE_SAFE_CLOSE_NOT_CONFIRMED_NO_FORCE_EXIT");
            }
        }catch(Exception e){PilotLog.Event("INTEGRATED_BOOTSTRAP_REJECTED",e.ToString());Exit();}
    }
}
internal sealed partial class IntegratedWindow:Window
{
    public Task InitialRender {get;}
    readonly ReleaseCatalog catalog;ReleaseSession session;readonly InstitutionalShell shell=new();
    readonly ContentControl hostInteractionGate=new(){HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch};
    readonly TextBlock state=InstitutionalVisualTokens.Text("Candidato integrado F13 · QA visual pendiente",12,true);
    readonly Dictionary<string,DetachedExcelWindow> editors=new(StringComparer.Ordinal);
    CentroViewModel? center;string page="Inicio";bool busy,rendering,refreshAgain,hostClosing;
    B09File? currentActionAdmission;
    public IntegratedWindow(ReleaseCatalog profiles,ReleaseSession active)
    {
        catalog=profiles;session=active;Title="Sistema de Marcaciones CBA · SR3 · Prueba local";hostInteractionGate.Content=shell.Root;Content=hostInteractionGate;
        if(AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)presenter.Maximize();
        LoadPopulationCatalog();
        Hook();shell.Navigate+=p=>{if(!AcceptsUiWork)return;page=p;_=Render();};
        AppWindow.Closing+=HostClosing;
        Closed+=(_,_)=>{hostClosing=true;Unhook();PilotLog.Event("INTEGRATED_WINDOW_CLOSED");};InitialRender=Render();
    }
    void Hook(){session.Inner.Projects.Changed+=Changed;session.Inner.Committed+=Changed;session.Attendance.Committed+=Changed;}
    void Unhook(){session.Inner.Projects.Changed-=Changed;session.Inner.Committed-=Changed;session.Attendance.Committed-=Changed;}
    void Changed(CanonicalChange change){if(!AcceptsUiWork)return;DispatcherQueue.TryEnqueue(()=>{if(!AcceptsUiWork)return;state.Text="Guardado confirmado · revisión "+change.Revision;_=Render();});}
    static void Name(DependencyObject control,string label)=>AutomationProperties.SetName(control,label);
    static TextBlock Text(string text,int size=12,bool bold=false)=>InstitutionalVisualTokens.Text(text,size,bold);
    Button Action(string label,Func<Task> action)
    {
        var caption=ActionLabel(label);var button=new Button{Content=caption,IsEnabled=!busy&&AcceptsUiWork};Name(button,caption);
        button.Click+=(_,_)=>{_=hostUi.Track(InvokeAction());};
        async Task InvokeAction()
        {
            if(busy||!AcceptsUiWork)return;busy=true;button.IsEnabled=false;state.Text="Operación en curso…";
            try
            {
                await HostWork(label,async()=>
                {
                    if(label is "Importar biométrico QA admitido" or "Procesar C# QA" or "Generar matriz principal" or "Exportar revisión guardada QA"
                        or "Procesar motor específico QA" or "Generar matriz de la población" or "Exportar matriz y revisión QA")
                    {state.Text="Verificando recursos antes de la operación";currentActionAdmission=await NativeActionAdmission.FromEnvironment();}
                    using(OperationLifetime.EnterCommit())await action();
                    if(!hostClosing)state.Text="Operación de prueba completada";
                });
            }
            catch(OperationCanceledException){state.Text="Operación cancelada antes de iniciar una nueva escritura.";PilotLog.Event("SR1_HOST_OPERATION_CANCELLED",label);}
            catch(Exception e){state.Text="No confirmado: "+e.Message;PilotLog.Event("INTEGRATED_ACTION_REJECTED",e.ToString());}
            finally{currentActionAdmission=null;busy=false;if(AcceptsUiWork)await Render();}
        }
        return button;
    }
    async Task Switch(int index)
    {
        if(busy||!AcceptsUiWork)throw new InvalidOperationException("OPERATION_IN_PROGRESS_NO_PROJECT_SWITCH");
        if(HasUncommittedViewDraft())throw new InvalidOperationException("SAVE_OR_CANCEL_DRAFT_BEFORE_PROJECT_SWITCH");
        busy=true;try
        {
            var next=await Task.Run(()=>ReleaseSession.Open(catalog.Entries[index].Configuration));Unhook();session=next;center=null;Hook();PilotLog.Event("INTEGRATED_PROFILE_SWITCHED",catalog.Entries[index].Name);
        }finally{busy=false;}
    }
    void OpenEditor()
    {
        var r=session.Attendance.Registrations.LastOrDefault()??throw new InvalidOperationException("Primero genera la matriz desde una ejecución nativa admitida.");
        var key=session.Inner.Configuration.ProjectRoot+"|"+r.MatrixId;
        if(editors.TryGetValue(key,out var existing)){existing.Activate();return;}
        var p=session.Attendance.ReadProfile(r);var editor=new DetachedExcelWindow(session.Inner.Projects,session.Inner.Handle,p,r.Workbook.Asset.FileName,
            Path.Combine(session.Inner.Configuration.ProjectRoot,"editor_exports","TEST_FIXTURE_ONLY"));
        editors.Add(key,editor);editor.Closed+=(_,_)=>EditorClosed(key);editor.Activate();
    }
    void EditorClosed(string key)
    {
        if(!editors.Remove(key)||hostClosing)return;
        // Rebuild selectors from actual live windows, preserving any Center draft through Render's existing checks.
        _=Render();
    }
    Task Render()=>hostUi.Track(RenderTracked());
    async Task RenderTracked()
    {
        if(!AcceptsUiWork)return;
        try{await HostWork("HOST_RENDER",RenderCore);}catch(OperationCanceledException){ /* Shutdown prevents new view work. */ }
    }
    async Task RenderCore()
    {
        if(!AcceptsUiWork)return;
        if(populationSession!=null){await RenderPopulation();return;}
        if(rendering){refreshAgain=true;return;}rendering=true;
        try
        {
            var model=await Task.Run(()=>session.Inner.ReadModel());if(!AcceptsUiWork)return;var period=session.Inner.Configuration.Period;
            shell.Period($"{period.Start:dd/MM/yyyy} — {period.End:dd/MM/yyyy}");shell.State("Sandbox · LegacyReadOnly · SQL SHADOW");
            var root=shell.Work;root.Children.Clear();root.RowDefinitions.Clear();
            foreach(var height in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})root.RowDefinitions.Add(new(){Height=height});
            var heading=new StackPanel{Spacing=6};heading.Children.Add(RoutePicker());heading.Children.Add(Text(page=="Inicio"?"Control mensual":page,21,true));
            heading.Children.Add(Text($"Modo de prueba · Revisión {session.Inner.Handle.Work.Revision}"));
            var picker=new ComboBox{ItemsSource=catalog.Entries.Select(e=>e.Name+" · "+e.Configuration.Read<ReleaseConfiguration>().BaseConfiguration.Read<B09Configuration>().Period.Start.ToString("dd/MM/yyyy")+" — "+e.Configuration.Read<ReleaseConfiguration>().BaseConfiguration.Read<B09Configuration>().Period.End.ToString("dd/MM/yyyy")).ToArray(),MinWidth=440,
                SelectedIndex=-1,IsEnabled=!busy&&editors.Count==0};
            // Identify the active root explicitly; never switch using display text or a fuzzy name.
            picker.SelectedIndex=Array.FindIndex(catalog.Entries,e=>e.Configuration.Read<ReleaseConfiguration>().BaseConfiguration.Path==session.Configuration.BaseConfiguration.Path);
            Name(picker,"Proyecto, período y perfil QA exactos");picker.SelectionChanged+=(_,_)=>{_=hostUi.Track(SwitchFromPicker());};
            async Task SwitchFromPicker(){if(picker.SelectedIndex<0||busy||!AcceptsUiWork)return;try{await HostWork("SWITCH_PROJECT",()=>Switch(picker.SelectedIndex));if(AcceptsUiWork)await Render();}catch(Exception e){state.Text=e.Message;}}
            var theme=new ComboBox{ItemsSource=new[]{"Claro","Oscuro","Automático"},SelectedIndex=shell.Root.RequestedTheme==ElementTheme.Dark?1:shell.Root.RequestedTheme==ElementTheme.Default?2:0,Width=140};Name(theme,"Tema de interfaz");theme.SelectionChanged+=(_,_)=>shell.Root.RequestedTheme=theme.SelectedIndex switch{0=>ElementTheme.Light,1=>ElementTheme.Dark,_=>ElementTheme.Default};
            var selectors=new PilotWrapPanel();selectors.Children.Add(picker);selectors.Children.Add(theme);heading.Children.Add(selectors);root.Children.Add(heading);
            var actions=new PilotWrapPanel();
            actions.Children.Add(Action("Importar biométrico QA admitido",()=>Task.Run(()=>session.ReimportExactQaBiometrics("F13_UI_QA",DateTimeOffset.UtcNow))));
            actions.Children.Add(Action("Procesar C# QA",async()=>
            {
                var admission=currentActionAdmission??throw new InvalidOperationException("CURRENT_ACTION_ADMISSION_REQUIRED");
                await Task.Run(()=>session.ExecutePinnedInputs(admission,"F13_UI_QA",DateTimeOffset.UtcNow));
            }));
            actions.Children.Add(Action("Generar matriz principal",()=>Task.Run(()=>session.Generate("F13_UI_QA",DateTimeOffset.UtcNow))));
            actions.Children.Add(Action("Abrir editor independiente",()=>{OpenEditor();return Task.CompletedTask;}));
            actions.Children.Add(Action("Reanudar",()=>Task.Run(()=>session.Inner.Projects.Refresh(session.Inner.Handle))));
            Grid.SetRow(actions,1);root.Children.Add(actions);

            var sideText=Text("Selecciona un resultado para ver la traza completa.",11);sideText.IsTextSelectionEnabled=true;
            var side=new StackPanel{Spacing=8};side.Children.Add(sideText);var technical=TechnicalDetail(side);
            var detail=new ScrollViewer{Content=new Border{Child=side,Padding=new(10)},VerticalScrollMode=ScrollMode.Enabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollMode=ScrollMode.Disabled,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,IsVerticalScrollChainingEnabled=false,IsTabStop=true,UseSystemFocusVisuals=true,MinHeight=0};Name(detail,"Detalle causal completo, controles y revisión pendiente. Desplazamiento vertical");
            var table=new InstitutionalTable("Integración F13 · "+page);
            var bundle=NativeAttendanceCoordinator.Collect(session.Inner);var facts=bundle.Facts.Where(f=>f.Classification==(selectedPopulation=="OPADMIN"?"OPERATIVO_EN_LABORES_ADMINISTRATIVAS":"ADMINISTRATIVO")).ToArray();
            if(page=="Novedades")
            {
                center??=session.Attendance.Center("F13_UI_QA");var vm=center;
                // Preserve a user's in-flight draft and its original CAS revision across an external commit.
                var hasDraft=vm.Selected is {} selected&&(vm.Note!=(selected.Decision?.Note??"")||vm.Responsible!=(selected.Decision?.Responsible??"")||vm.DraftState!=selected.State);
                if(!hasDraft)await vm.Open();
                table.Configure(new("Servidor",170),new("Fecha",100),new("Resultado",235),new("Estado",150));
                table.Rows(vm.Rows.Select(r=>new PresentationRow(r.Item.Id,[r.Item.PersonId??"Identidad pendiente",r.Item.Date?.ToString("dd/MM/yyyy")??"Sin fecha",ResultLabel(r.Item.Reason),CentroViewModel.StateLabel(r.State)],r)));
                var who=new TextBox{Header="Responsable",MaxLength=200,Text=vm.Responsible};var note=new TextBox{Header="Observaciones administrativas",AcceptsReturn=true,MaxLength=4000,TextWrapping=TextWrapping.Wrap,Text=vm.Note};
                var status=new ComboBox{Header="Estado de revisión",ItemsSource=Enum.GetValues<ManagementState>().Select(CentroViewModel.StateLabel).ToArray(),SelectedIndex=Array.IndexOf(Enum.GetValues<ManagementState>(),vm.DraftState)};
                who.TextChanged+=(_,_)=>{if(AcceptsUiWork)vm.Responsible=who.Text;};note.TextChanged+=(_,_)=>{if(AcceptsUiWork)vm.Note=note.Text;};
                status.SelectionChanged+=(_,_)=>{if(AcceptsUiWork&&status.SelectedIndex>=0)vm.DraftState=Enum.GetValues<ManagementState>()[status.SelectedIndex];};
                table.Selection+=row=>{if(row==null||!AcceptsUiWork)return;if(HasCenterDraft()){state.Text="Borrador del caso activo conservado. Guarda o revierte sus campos antes de cambiar la selección.";return;}vm.Select(row.Key);who.Text=vm.Responsible;note.Text=vm.Note;status.SelectedIndex=Array.IndexOf(Enum.GetValues<ManagementState>(),vm.DraftState);sideText.Text=$"{row.C0} · {row.C1}";technical.Text=JsonSerializer.Serialize(new{Item=vm.Selected,Evidence=vm.Evidence,History=vm.History},B07Contract.Wire);};
                side.Children.Add(who);side.Children.Add(status);side.Children.Add(note);
                side.Children.Add(Action("Guardar gestión administrativa",async()=>{vm.Responsible=who.Text;vm.Note=note.Text;vm.DraftState=status.SelectedIndex>=0?Enum.GetValues<ManagementState>()[status.SelectedIndex]:throw new InvalidOperationException("Selecciona un estado de gestión");state.Text="Guardando gestión";if(!await vm.Save())throw new InvalidOperationException(vm.Message);await session.Inner.Projects.Refresh(session.Inner.Handle);}));
            }
            else if(page is "Revisión" or "Exportación")
            {
                table.Configure(new("Matriz principal",260),new("Revisión origen",120),new("Estado",270));
                table.Rows(session.Attendance.Registrations.Select(r=>new PresentationRow(r.MatrixId,[r.Workbook.Asset.Profile,r.SourceRevision.ToString(),ResultLabel(r.Workbook.Status)],r)));
                table.Selection+=r=>{if(r!=null){sideText.Text=$"{r.C0} · {r.C1}";technical.Text=JsonSerializer.Serialize(r.Source,B07Contract.Wire);}};
                actions.Children.Add(Action("Exportar revisión guardada QA",async()=>{var r=session.Attendance.Registrations.LastOrDefault()??throw new InvalidOperationException("No hay matriz registrada");var p=session.Attendance.ReadProfile(r);var receipt=await CompletionExports.Export(session.Inner.Projects,session.Inner.Handle,p,r.Workbook.Asset.FileName,Path.Combine(session.Inner.Configuration.ProjectRoot,"ui_exports","TEST_FIXTURE_ONLY"),session.Inner.Handle.Work.Revision,"F13_UI_QA",DateTimeOffset.UtcNow);PilotLog.Event("COMPLETION_EXPORT_DURABLY_CONFIRMED",JsonSerializer.Serialize(receipt,B07Contract.Wire));}));
                var exported=SistemaMarcaciones.Monthly.Projects.ProjectEvents.Read<CompletionExportReceipt>(session.Inner.Handle.Work,CompletionExports.Kind).LastOrDefault();
                sideText.Text=exported==null?"Exportación integrada QA: matriz original y revisión manual con filas de Valores. No certificada. Sin cálculo D08.":$"Exportación durable confirmada · revisión {exported.ConfirmedRevision} · origen {exported.SourceRevision}\n\nAsistencia: {exported.Attendance.Path}\n\nRevisión: {exported.Review.Path}\n\nManifest: {exported.Manifest.Path}\n\nSin cálculo D08. NO CERTIFICADA.";
            }
            else
            {
                table.Configure(new("Servidor",170),new("Fecha",100),new("Clasificación",200),new("Resultado / estado",240),new("Minutos",95));
                var rows=facts.Select(f=>new PresentationRow(f.CaseId,[f.PersonId,f.Date.ToString("dd/MM/yyyy"),PopulationLabel(selectedPopulation),ResultLabel(f.Result),f.Minutes?.ToString()??"No determinado"],f)).ToList();
                rows.AddRange(model.Outcomes.Where(o=>o.Facts.Length==0).Select(o=>new PresentationRow(o.ReceiptId??o.PersonId,[o.PersonId,o.Date.ToString("dd/MM/yyyy"),"Alcance QA",o.Label,"No aplica / no determinado"],o)));
                table.Rows(rows);table.Selection+=r=>{if(r!=null){sideText.Text=$"{r.C0} · {r.C1}";technical.Text=JsonSerializer.Serialize(r.Source,B07Contract.Wire);}};
                sideText.Text=rows.Count==0?"Todavía no hay resultados en este proyecto.":"Selecciona un resultado para consultar su detalle.";
                technical.Text=$"{facts.Length} resultados guardados · {bundle.Completions.Length} registros de procesamiento.\nGuardia: siete expedientes reales tienen evidencia pendiente.\nCobertura temporal y aprobación visual pendientes.";
                if(page=="Inicio" && rows.Count==0)
                    heading.Children.Add(Text("Para probar atrasos: carga los datos de ejemplo, procesa las marcaciones, genera la matriz y ábrela."));
            }
            var pending=CrossMatrixReview.For(session.Inner.Handle.Work).Pending;
            side.Children.Add(Text("Revisión pendiente / Valores a Descontar",13,true));
            side.Children.Add(Text(pending.Count==0?"Sin cambios manuales pendientes.":string.Join("\n\n",pending.Select(p=>$"{p.PersonId??p.AnonymousLabel} · {p.Date:dd/MM/yyyy}\n{p.Original} → {p.Manual}\nCAMBIO PENDIENTE DE ACREDITACIÓN\n{p.EconomicImpact}. Importe original sin cambios.")),11));
            PlaceTableAndDetails(root,table,detail);
            if(state.Parent is Panel parent)parent.Children.Remove(state);Grid.SetRow(state,3);root.Children.Add(state);PilotLog.Event("INTEGRATED_VIEW_READY",page);
        }
        catch(Exception e){state.Text="Vista no confirmada: "+e.Message;PilotLog.Event("INTEGRATED_VIEW_REJECTED",e.ToString());}
        finally{rendering=false;if(refreshAgain){refreshAgain=false;if(AcceptsUiWork)_=Render();}}
    }
}
