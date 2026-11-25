using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SistemaMarcaciones.Centro.Presentation;
using SistemaMarcaciones.F13.Integrated.Native;
using SistemaMarcaciones.F13.Populations;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Monthly.Host.NativeIntegration;
using SistemaMarcaciones.Monthly.Management;
using SistemaMarcaciones.Monthly.Native.Outcomes;

namespace SistemaMarcaciones.Desktop.Pilot.WinUI;

public sealed record PopulationRoute(string Population,string Name,string Coverage,B09File? Configuration);
public sealed record AllPopulationCatalog(string Contract,PopulationRoute[] Routes);

internal sealed partial class IntegratedWindow
{
    static string ExcelCompatiblePopulationFiles(PopulationExportReceipt receipt,string destination)
    {
        var original=ExcelPackageCompatibility.Create(new(Path.Combine(destination,receipt.Original.FileName),receipt.Original.SHA256),destination,receipt.Population+"_ORIGINAL");
        var review=receipt.Manual is {} manual
            ?ExcelPackageCompatibility.Create(new(Path.Combine(destination,manual.FileName),manual.SHA256),destination,receipt.Population+"_REVIEW")
            :original;
        PilotLog.Event("SR3_POPULATION_EXCEL_PRESENTATIONS_VERIFIED",JsonSerializer.Serialize(new{OriginalReceipt=receipt,Original=original,Review=review,NoEconomicCalculation=true}));
        return $"Exportación de {receipt.Population} confirmada · revisión {receipt.ConfirmedRevision}. Archivos Excel: {original.Workbook.Path} · {review.Workbook.Path}. Sin cálculo económico. NO CERTIFICADA.";
    }
    AllPopulationCatalog allPopulations=null!;
    MultiPopulationSession? populationSession;
    string selectedPopulation="ADMIN";
    bool HasCenterDraft()
    {
        var vm=center;
        return vm?.Selected is {} selected&&(vm.Note!=(selected.Decision?.Note??"")||vm.Responsible!=(selected.Decision?.Responsible??"")||vm.DraftState!=selected.State);
    }
    bool HasUncommittedViewDraft()
    {
        return editors.Values.Any(e=>e.HasPendingDraft)||HasCenterDraft();
    }
    void LoadPopulationCatalog()
    {
        var catalogPin=new B09File(Environment.GetEnvironmentVariable("CBA_F13_ALL_POPULATIONS_CATALOG")??throw new InvalidDataException("FOUR_ROUTE_CATALOG_REQUIRED"),
            Environment.GetEnvironmentVariable("CBA_F13_ALL_POPULATIONS_CATALOG_SHA256")??throw new InvalidDataException("FOUR_ROUTE_CATALOG_PIN_REQUIRED"));
        allPopulations=catalogPin.Read<AllPopulationCatalog>();
        if(allPopulations.Contract!="F13_ALL_POPULATIONS_NATIVE_RELEASE_QA_V1"||allPopulations.Routes.Length!=4
            ||!allPopulations.Routes.Select(r=>r.Population).Order().SequenceEqual(new[]{"ADMIN","DIRECTORES","GUARDIA","OPADMIN"}))
            throw new InvalidDataException("FOUR_DISTINCT_EXPLICIT_ROUTES_REQUIRED");
        foreach(var r in allPopulations.Routes)
        {
            if(r.Coverage is not ("VALIDATED_SUBSET" or "TECHNICALLY_IMPLEMENTED_PENDING_QA" or "BLOCKED_MISSING_EVIDENCE" or "NOT_SUPPORTED"))throw new InvalidDataException("ROUTE_COVERAGE_REQUIRED");
            if(r.Population is "DIRECTORES" or "GUARDIA")
            {
                if(r.Configuration==null)throw new InvalidDataException("SPECIFIC_CAUSAL_PORT_REQUIRED_NO_ADMIN_FALLBACK");
                var c=r.Configuration.Read<MultiPopulationConfiguration>();
                if(c.Population!=r.Population)throw new InvalidDataException("ROUTE_CLASSIFICATION_CONFIG_MISMATCH");
            }
        }
        Closed+=(_,_)=>{if(populationSession!=null){populationSession.Projects.Changed-=Changed;populationSession.Committed-=Changed;}};
    }
    FrameworkElement RoutePicker()
    {
        var box=new ComboBox{ItemsSource=allPopulations.Routes.Select(r=>PopulationLabel(r.Population)).ToArray(),
            SelectedIndex=Array.FindIndex(allPopulations.Routes,r=>r.Population==selectedPopulation),IsEnabled=!busy&&editors.Count==0&&!HasUncommittedViewDraft(),MinWidth=215};
        Name(box,"Población y cobertura real. Cuatro rutas con contratos separados");
        box.SelectionChanged+=(_,_)=>{_=hostUi.Track(SwitchPopulationFromPicker());};
        async Task SwitchPopulationFromPicker()
        {
            if(box.SelectedIndex<0||busy||!AcceptsUiWork)return;
            var next=allPopulations.Routes[box.SelectedIndex];if(next.Population==selectedPopulation)return;
            busy=true;
            try
            {
                await HostWork("SWITCH_POPULATION",async()=>
                {
                if(HasUncommittedViewDraft())throw new InvalidOperationException("SAVE_OR_CANCEL_DRAFT_BEFORE_POPULATION_SWITCH");
                MultiPopulationSession? opened=next.Population is "DIRECTORES" or "GUARDIA"?await Task.Run(()=>MultiPopulationSession.Open(next.Configuration!)):null;
                if(populationSession!=null){populationSession.Projects.Changed-=Changed;populationSession.Committed-=Changed;}else Unhook();
                populationSession=opened;selectedPopulation=next.Population;center=null;page="Inicio";shell.Select("Inicio",notify:false);
                if(populationSession!=null){populationSession.Projects.Changed+=Changed;populationSession.Committed+=Changed;}else{Hook();await session.Inner.Projects.Refresh(session.Inner.Handle);}
                PilotLog.Event("FOUR_POPULATION_ROUTE_OPENED",next.Population+";OpeningRunsNoKernel=True;"+next.Coverage);
                });
            }
            catch(Exception e){state.Text="Ruta no admitida: "+e.Message;PilotLog.Event("FOUR_POPULATION_ROUTE_REJECTED",e.ToString());}
            finally{busy=false;if(AcceptsUiWork)await Render();}
        }
        return box;
    }
    void OpenPopulationEditor()
    {
        var p=populationSession??throw new InvalidOperationException("POPULATION_SESSION_REQUIRED");
        var r=p.Registrations.LastOrDefault()??throw new InvalidOperationException("Primero genera la matriz del motor específico.");
        var key=p.Configuration.ProjectRoot+"|"+r.MatrixId;
        if(editors.TryGetValue(key,out var existing)){existing.Activate();return;}
        var profile=p.ReadProfile(r);
        var exportRoot=Path.Combine(p.Configuration.ProjectRoot,"editor_exports","TEST_FIXTURE_ONLY");
        var editor=new DetachedExcelWindow(p.Projects,p.Handle,profile,r.Workbook.Asset.FileName,exportRoot)
        {
            SpecificPopulationExport=async()=>
            {
                var receipt=await Task.Run(()=>p.Export(r,exportRoot,"F13_FOUR_ROUTE_EDITOR_QA",DateTimeOffset.UtcNow));
                return ExcelCompatiblePopulationFiles(receipt,exportRoot);
            }
        };
        editors.Add(key,editor);editor.Closed+=(_,_)=>EditorClosed(key);editor.Activate();
    }
    async Task RenderPopulation()
    {
        if(!AcceptsUiWork)return;
        if(rendering){refreshAgain=true;return;}rendering=true;
        try
        {
            var p=populationSession!;var cut=p.Configuration.Period;
            shell.Period($"{cut.Start:dd/MM/yyyy} — {cut.End:dd/MM/yyyy}");shell.State("Sandbox · Cuatro rutas · SQL SHADOW · NO CERTIFICADO");
            var root=shell.Work;root.Children.Clear();root.RowDefinitions.Clear();
            foreach(var h in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})root.RowDefinitions.Add(new(){Height=h});
            var header=new StackPanel{Spacing=6};header.Children.Add(RoutePicker());header.Children.Add(Text((page=="Inicio"?"Control mensual":page)+" · "+PopulationLabel(selectedPopulation),21,true));
            header.Children.Add(Text($"Modo de prueba · Revisión {p.Handle.Work.Revision}"));root.Children.Add(header);
            var actions=new PilotWrapPanel();
            actions.Children.Add(Action("Procesar motor específico QA",async()=>{if(HasCenterDraft())throw new InvalidOperationException("SAVE_OR_CANCEL_CENTER_DRAFT_BEFORE_PROCESSING");var gate=currentActionAdmission??throw new InvalidOperationException("CURRENT_ACTION_ADMISSION_REQUIRED");await Task.Run(()=>p.Execute(gate,p.Handle.Work.Revision,"F13_FOUR_ROUTE_UI_QA",DateTimeOffset.UtcNow));center=null;}));
            actions.Children.Add(Action("Generar matriz de la población",async()=>{await Task.Run(()=>p.Generate(p.Handle.Work.Revision,"F13_FOUR_ROUTE_UI_QA",DateTimeOffset.UtcNow));}));
            actions.Children.Add(Action("Abrir editor independiente",()=>{OpenPopulationEditor();return Task.CompletedTask;}));
            actions.Children.Add(Action("Reanudar",()=>Task.Run(()=>p.Projects.Refresh(p.Handle))));Grid.SetRow(actions,1);root.Children.Add(actions);

            var table=new InstitutionalTable("Ruta específica "+selectedPopulation+" · "+page);
            var detail=Text("Selecciona un resultado para consultar su detalle.",11);detail.IsTextSelectionEnabled=true;
            var side=new StackPanel{Spacing=8};side.Children.Add(detail);var technical=TechnicalDetail(side);
            if(page=="Novedades")
            {
                center??=p.Center("F13_FOUR_ROUTE_UI_QA");var vm=center;
                var draft=vm.Selected is {} selected&&(vm.Note!=(selected.Decision?.Note??"")||vm.Responsible!=(selected.Decision?.Responsible??"")||vm.DraftState!=selected.State);
                if(!draft)await vm.Open();
                table.Configure(new("Servidor",170),new("Fecha",100),new("Resultado",270),new("Estado",140));
                table.Rows(vm.Rows.Select(r=>new PresentationRow(r.Item.Id,[r.Item.PersonId??"Sin identidad",r.Item.Date?.ToString("dd/MM/yyyy")??"Sin fecha",ResultLabel(r.Item.Reason),CentroViewModel.StateLabel(r.State)],r)));
                var who=new TextBox{Header="Responsable",Text=vm.Responsible,MaxLength=200};var note=new TextBox{Header="Observaciones administrativas",Text=vm.Note,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxLength=4000};
                var status=new ComboBox{Header="Estado de revisión",ItemsSource=Enum.GetValues<ManagementState>().Select(CentroViewModel.StateLabel).ToArray(),SelectedIndex=Array.IndexOf(Enum.GetValues<ManagementState>(),vm.DraftState)};
                who.TextChanged+=(_,_)=>{if(AcceptsUiWork)vm.Responsible=who.Text;};note.TextChanged+=(_,_)=>{if(AcceptsUiWork)vm.Note=note.Text;};status.SelectionChanged+=(_,_)=>{if(AcceptsUiWork&&status.SelectedIndex>=0)vm.DraftState=Enum.GetValues<ManagementState>()[status.SelectedIndex];};
                table.Selection+=r=>{if(r==null||!AcceptsUiWork)return;if(HasCenterDraft()){state.Text="Borrador del caso activo conservado. Guarda o revierte sus campos antes de cambiar la selección.";return;}vm.Select(r.Key);who.Text=vm.Responsible;note.Text=vm.Note;status.SelectedIndex=Array.IndexOf(Enum.GetValues<ManagementState>(),vm.DraftState);detail.Text=$"{r.C0} · {r.C1}";technical.Text=JsonSerializer.Serialize(new{Item=vm.Selected,vm.Evidence,vm.History},B07Contract.Wire);};
                side.Children.Add(who);side.Children.Add(status);side.Children.Add(note);
                side.Children.Add(Action("Guardar gestión administrativa",async()=>{if(!await vm.Save())throw new InvalidOperationException(vm.Message);await p.Projects.Refresh(p.Handle);}));
            }
            else
            {
                table.Configure(new("Servidor",180),new("Fecha",100),new("Clasificación",200),new("Resultado",260),new("Minutos",100));
                table.Rows(p.Facts.Select(f=>new PresentationRow(f.CaseId,[f.PersonId,f.Date.ToString("dd/MM/yyyy"),ResultLabel(f.Classification),ResultLabel(f.Result),f.Minutes?.ToString()??"No determinado"],f)));
                table.Selection+=r=>{if(r!=null){detail.Text=$"{r.C0} · {r.C1}";technical.Text=JsonSerializer.Serialize(new{Result=r.Source,Units=p.Completions},B07Contract.Wire);}};
                side.Children.Add(Text(selectedPopulation=="DIRECTORES"
                    ?"Régimen / motor específico: Director · 480 minutos netos. La clasificación temporal QA se conserva como dato de la fuente; no selecciona un motor OpAdmin."
                    :"Régimen / motor específico: Guardia. La clasificación temporal QA se conserva separada de la estrategia de servicio.",12,true));
                side.Children.Add(Text("UNIDADES COMPLETADAS / BLOQUEADAS · CERO ≠ DESCONOCIDO",12,true));
                technical.Text=JsonSerializer.Serialize(p.Completions,B07Contract.Wire);
                if(page is "Revisión" or "Exportación")actions.Children.Add(Action("Exportar matriz y revisión QA",async()=>{var r=p.Registrations.LastOrDefault()??throw new InvalidOperationException("No hay matriz generada");var destination=Path.Combine(p.Configuration.ProjectRoot,"ui_exports","TEST_FIXTURE_ONLY");var receipt=await p.Export(r,destination,"F13_FOUR_ROUTE_UI_QA",DateTimeOffset.UtcNow);_=ExcelCompatiblePopulationFiles(receipt,destination);}));
            }
            side.Children.Add(Text("Guardia: siete expedientes reales siguen pendientes de evidencia. Esta pantalla muestra datos de prueba.",11));
            var pending=CrossMatrixReview.For(p.Handle.Work).Pending;side.Children.Add(Text("Revisión manual pendiente: "+pending.Count+" persona-fecha. Importes originales conservados; sin D08.",11));
            var scroll=new ScrollViewer{Content=new Border{Child=side,Padding=new(10)},VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,IsVerticalScrollChainingEnabled=false,IsTabStop=true};Name(scroll,"Detalle causal de la población específica y unidades completadas. Desplazamiento vertical");
            PlaceTableAndDetails(root,table,scroll);
            if(state.Parent is Panel parent)parent.Children.Remove(state);Grid.SetRow(state,3);root.Children.Add(state);PilotLog.Event("FOUR_ROUTE_VIEW_READY",selectedPopulation+";"+page+";Revision="+p.Handle.Work.Revision);
        }
        catch(Exception e){state.Text="Vista de población no confirmada: "+e.Message;PilotLog.Event("FOUR_ROUTE_VIEW_REJECTED",e.ToString());}
        finally{rendering=false;if(refreshAgain){refreshAgain=false;if(AcceptsUiWork)_=Render();}}
    }
}
