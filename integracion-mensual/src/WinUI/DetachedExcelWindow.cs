using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Monthly.Projects;
using SistemaMarcaciones.F13.Integrated.Native;

namespace SistemaMarcaciones.Desktop.Pilot.WinUI;
public sealed partial class DetachedExcelWindow:Window
{
    public string MatrixId=>profile.Draft.Id;
    public string ProjectId=>project.Id;
    public bool HasPendingDraft=>buffer.Pending.Length>0;
    public Func<Task<string>>? SpecificPopulationExport {get;init;}
    public Func<Task>? ProductActionCheck {get;init;}
    public Func<string,string,Task<(string Summary,string Technical)?>>? SpecificCausalDetail {get;init;}
    public Func<string,string,Task<(string Summary,string Technical)?>>? SpecificDocumentDetail {get;init;}
    public string? SpecificHeaderCaption {init{if(value!=null)periodHeading.Text=value;}}
    readonly TextBlock periodHeading;
    readonly ProjectCoordinator coordinator;readonly ProjectHandle project;readonly WorkbookProfile profile;readonly string source,destination;
    readonly bool historicalReference;
    readonly Grid root=new(){RowSpacing=5,Padding=new(10)};readonly TextBox address=new(){Width=115};
    readonly ContentControl editorInteractionGate=new(){HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch};
    readonly TextBox content=new(){MinWidth=400,MaxLength=32767};readonly TextBox reason=new(){Text="Revisión administrativa local",MinWidth=220,MaxLength=4000};
    readonly TextBox find=new(){PlaceholderText="Buscar contenido",Width=210};readonly TextBlock state=InstitutionalVisualTokens.Text("Guardado",12,true);
    readonly TextBlock cellDetail=InstitutionalVisualTokens.Text("",11);readonly ComboBox tabs=new(){MinWidth=340};
    readonly ComboBox theme=new(){ItemsSource=new[]{"Claro","Oscuro","Automático"},SelectedIndex=0,MinWidth=140};
    readonly ComboBox layer=new(){ItemsSource=new[]{"Revisión local","Original / revisión administrativa"},SelectedIndex=0,MinWidth=260};
    readonly TextBlock manualWarning=InstitutionalVisualTokens.Text("Revisión local · Los cambios requieren acreditación. Los importes originales se conservan.",12,true);
    readonly CheckBox compact=new(){Content="Compactar filas vacías (solo vista)",IsChecked=true};
    readonly CheckBox fit=new(){Content="Ajustar texto (solo vista)",IsChecked=true};
    readonly Border viewport=new();readonly EditorBuffer buffer=new();readonly SemaphoreSlim serial=new(1,1);readonly DispatcherTimer watcher=new(){Interval=TimeSpan.FromSeconds(3)};
    PhysicalSheetCanvas? grid;bool setting,closed,manualDialog,refreshing;string sheet;double zoom;
    WorksheetInteractionIndex? interaction;HashSet<string> editableCells=new(StringComparer.Ordinal);
    IReadOnlyDictionary<string,ManualCellPresentation> displayedPresentation=new Dictionary<string,ManualCellPresentation>();string[] displayedMerges=[];
    IReadOnlyDictionary<string,ManualCellTarget> manualTargets=new Dictionary<string,ManualCellTarget>();
    int displayedRevision=-1,renderedRevision=-1;string displayedSheet="";Dictionary<string,string?> displayedValues=new(StringComparer.Ordinal);
    EditorDisplaySnapshot? displaySnapshot;
    MenuFlyout? activeOptions;
    CrossMatrixReview? reviewProjection;
    readonly Expander pendingEconomic=new(){Header="Cambios pendientes",IsExpanded=false,MaxHeight=210};
    public DetachedExcelWindow(ProjectCoordinator coordinator,ProjectHandle project,WorkbookProfile profile,string source,string destination)
    {
        this.coordinator=coordinator;this.project=project;this.profile=profile;this.source=source;this.destination=destination;sheet=profile.Draft.Sheets[0].Name;zoom=profile.Sheets[0].Zoom/100d;
        historicalReference=HistoricalFinalMatrices.IsHistorical(profile.Draft);
        if(historicalReference){layer.SelectedIndex=1;layer.IsEnabled=false;manualWarning.Text="MATRIZ DEFINITIVA HISTÓRICA · SOLO LECTURA Y COMPARACIÓN · Sin edición, autoguardado o exportación de revisión.";}
        Title="Sistema de Marcaciones CBA · Revisión de matriz";editorInteractionGate.Content=root;Content=editorInteractionGate;
        if(AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)presenter.Maximize();
        root.RequestedTheme=ElementTheme.Light;
        for(int i=0;i<9;i++)root.RowDefinitions.Add(new(){Height=i==4?new(1,GridUnitType.Star):GridLength.Auto});
        InstitutionalVisualTokens.Brush(root,Panel.BackgroundProperty,"SurfacePrimary");
        periodHeading=InstitutionalVisualTokens.Text($"Matriz de asistencia · {project.Service.ActiveProject!.Period.Start:dd/MM/yyyy} — {project.Service.ActiveProject.Period.End:dd/MM/yyyy}",16,true);Add(0,periodHeading);
        Add(1,Line(Button("Guardar",Save),Button("Deshacer",()=>History(false)),Button("Rehacer",()=>History(true)),
            Button("Opciones de celda",CellOptions),Button("Consultar documento exacto",DocumentDetail,"Consultar respaldo"),Button("Exportar revisión guardada",Export,"Exportar a Excel")));
        var formula=new Grid{ColumnSpacing=8};formula.ColumnDefinitions.Add(new(){Width=GridLength.Auto});formula.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
        formula.Children.Add(address);Grid.SetColumn(content,1);formula.Children.Add(content);Add(2,formula);
        Add(3,Line(find,Button("Buscar",Search),Button("−",()=>Zoom(-.1)),Button("+",()=>Zoom(.1)),
            Button("← Desplazar",()=>{grid?.ScrollHorizontal(-320);return Task.CompletedTask;}),Button("Desplazar →",()=>{grid?.ScrollHorizontal(320);return Task.CompletedTask;}),state));Add(4,viewport);
        tabs.ItemsSource=profile.Draft.Sheets.Select(s=>s.Name).ToArray();tabs.SelectedItem=sheet;Add(5,Line(tabs,compact,fit));
        Add(6,manualWarning);
        var more=new StackPanel{Spacing=8};
        more.Children.Add(Line(Button("Copiar rango",Copy),Button("Pegar rango",Paste),Button("Comparar conflicto",Compare),Button("Registrar propuesta",Proposal),Button("Detalle causal completo",ForwardDetail),theme,layer,reason));
        more.Children.Add(cellDetail);
        more.Children.Add(InstitutionalVisualTokens.Text($"Referencia técnica de la matriz · {profile.Draft.Profile}",11));
        var limits=new Expander{Header="Protección y límites del libro",Content=InstitutionalVisualTokens.Text(string.Join("\n",profile.Limitations)+
            "\nVista compacta y ajuste de texto son reversibles: desmárcalos para ver el formato completo. Coordenadas A1, source y exportación no se modifican."+
            "\nTexto fuente, resultados, fórmulas y estructura están protegidos. F2 edita una observación autorizada o abre las opciones de revisión manual de una celda persona/fecha elegible. Toda revisión manual es separada del resultado original y requiere motivo y confirmación; no aprueba documentos ni reevalúa reglas laborales. Las fórmulas y valores económicos permanecen originales; no se certifican como concordantes con la revisión manual.",11)};
        more.Children.Add(limits);
        Add(7,new Expander{Header="Más opciones de revisión y ayuda",Content=new ScrollViewer{Content=more,MaxHeight=220},HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch});
        Add(8,pendingEconomic);Name(pendingEconomic,"Cambios manuales pendientes relacionados con Valores a descontar, sin nuevos importes");
        Name(address,"Coordenada Excel A1");Name(content,"Barra de contenido de celda. Fórmulas protegidas.");Name(reason,"Motivo de edición administrativa");Name(tabs,"Hojas del workbook en orden original");Name(find,"Buscar dentro de la hoja");
        Name(theme,"Tema de la ventana del editor");Name(compact,"Compactar espacios de plantilla comprobados sin modificar el archivo");Name(fit,"Ajustar texto en pantalla sin modificar el archivo");
        Name(layer,"Comparar resultado original y revisión manual separada de sandbox");Name(manualWarning,"Advertencia: revisión manual no acreditada; totales y valores económicos no recalculados");
        layer.SelectionChanged+=async(_,_)=>await Safe(async()=>{await Save();displayedRevision=-1;manualWarning.Text=layer.SelectedIndex==0?
            "Revisión local · Los cambios requieren acreditación. Los importes originales se conservan.":
            "Vista original · Para ver los cambios de prueba, selecciona «Revisión local».";await PrepareDisplay();grid?.Refresh();if(grid!=null)Select(grid.Coordinate,false);RefreshWorkingRowsView();});
        theme.SelectionChanged+=(_,_)=>{root.RequestedTheme=theme.SelectedIndex switch{0=>ElementTheme.Light,1=>ElementTheme.Dark,_=>ElementTheme.Default};PilotLog.Event("EDITOR_THEME",theme.SelectedItem?.ToString()??"Automático");};
        compact.Checked+=async(_,_)=>await Safe(ViewOptions);compact.Unchecked+=async(_,_)=>await Safe(ViewOptions);
        fit.Checked+=async(_,_)=>await Safe(ViewOptions);fit.Unchecked+=async(_,_)=>await Safe(ViewOptions);
        content.TextChanged+=(_,_)=>
        {
            if(setting||content.IsReadOnly||grid==null||!AcceptsEditorWork)return;
            var text=content.Text;
            // WinUI can deliver a programmatic TextChanged after Select has released setting.
            // A canonical-value echo is not a draft; repeated events must not replace a real draft or reset CAS.
            if(buffer.Pending.Length==0)
            {
                if(string.Equals(text,CurrentCellValue(grid.Coordinate)??"",StringComparison.Ordinal))return;
                buffer.Begin(project.DisplayWork.Revision,[new(sheet,grid.Coordinate,text)]);
            }
            else
            {
                if(buffer.Pending.Length==1&&buffer.Pending[0].Sheet==sheet&&buffer.Pending[0].Coordinate==grid.Coordinate&&
                    string.Equals(buffer.Pending[0].Value??"",text,StringComparison.Ordinal))return;
                buffer.Update([new(sheet,grid.Coordinate,text)]);
            }
            ShowState();
        };
        content.KeyDown+=async(_,e)=>
        {
            if(!AcceptsEditorWork){e.Handled=true;return;}
            if(e.Key==VirtualKey.Enter){e.Handled=true;await Safe(async()=>{await Save();grid!.Move(1,0);});}
            else if(e.Key==VirtualKey.Escape){e.Handled=true;buffer.DiscardExplicitly(project.DisplayWork.Revision);Select(grid!.Coordinate,false);ShowState();}
            else if(e.Key==VirtualKey.Tab){e.Handled=true;await Safe(async()=>{await Save();grid!.Move(0,PhysicalSheetCanvas.Modifier(VirtualKey.Shift)?-1:1);});}
        };
        content.LostFocus+=async(_,_)=>{if(buffer.Pending.Length>0&&!buffer.Conflict)await Safe(Save);};
        address.KeyDown+=async(_,e)=>{if(e.Key==VirtualKey.Enter){e.Handled=true;await Safe(async()=>{await Save();grid!.Select(address.Text.Trim().ToUpperInvariant());grid.Reveal(grid.Coordinate);});}};
        tabs.SelectionChanged+=async(_,_)=>
        {
            if(setting||tabs.SelectedItem==null||tabs.SelectedItem.ToString()==sheet)return;
            await Safe(async()=>{await Save();sheet=tabs.SelectedItem.ToString()!;ShowSheet();});
        };
        root.KeyDown+=async(_,e)=>
        {
            if(!AcceptsEditorWork){e.Handled=true;return;}
            if(e.OriginalSource is TextBox||grid==null)return;
            var focused=e.OriginalSource as DependencyObject;bool sheetFocus=false;
            while(focused!=null){if(ReferenceEquals(focused,grid.Root)){sheetFocus=true;break;}focused=Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(focused);}
            if(!sheetFocus)return;
            bool ctrl=PhysicalSheetCanvas.Modifier(VirtualKey.Control),shift=PhysicalSheetCanvas.Modifier(VirtualKey.Shift);
            Func<Task>? action=e.Key switch
            {VirtualKey.Left=>()=>Move(0,-1,shift),VirtualKey.Right=>()=>Move(0,1,shift),VirtualKey.Up=>()=>Move(-1,0,shift),VirtualKey.Down=>()=>Move(1,0,shift),
                VirtualKey.F2=>CellOptions,VirtualKey.C when ctrl=>Copy,VirtualKey.V when ctrl=>Paste,
                VirtualKey.Z when ctrl=>()=>History(false),VirtualKey.Y when ctrl=>()=>History(true),VirtualKey.S when ctrl=>Save,_=>null};
            if(action!=null){e.Handled=true;await Safe(action);}
        };
        root.KeyUp+=async(_,e)=>
        {
            if(!AcceptsEditorWork){e.Handled=true;return;}
            if(e.Key!=VirtualKey.Shift||e.OriginalSource is TextBox||grid==null||grid.Range().Length<2)return;
            var focused=e.OriginalSource as DependencyObject;
            while(focused!=null&&!ReferenceEquals(focused,grid.Root))focused=Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(focused);
            if(focused!=null){e.Handled=true;await Safe(CellOptions);}
        };
        coordinator.Changed+=OnCanonicalChange;
        EditorSaveProfile.Completed+=LogSaveProfile;
        watcher.Tick+=async(_,_)=>{if(!AcceptsEditorWork||refreshing)return;refreshing=true;try{await Safe(()=>Task.Run(()=>coordinator.Refresh(project)));}finally{refreshing=false;}};watcher.Start();
        root.Loaded+=async(_,_)=>await Safe(async()=>{await PrepareDisplay();ShowSheet();PilotLog.Event("DETACHED_LOADED",profile.Draft.Profile);});
        AppWindow.Closing+=EditorClosing;
        Closed+=(_,_)=>{closed=true;watcher.Stop();coordinator.Changed-=OnCanonicalChange;EditorSaveProfile.Completed-=LogSaveProfile;PilotLog.Event("DETACHED_CLOSED",profile.Draft.Profile);actualClose.TrySetResult(true);};
    }
    void Add(int row,FrameworkElement e){Grid.SetRow(e,row);root.Children.Add(e);}
    static void Name(DependencyObject e,string name)=>AutomationProperties.SetName(e,name);
    static PilotWrapPanel Line(params UIElement[] controls){var p=new PilotWrapPanel();foreach(var c in controls)p.Children.Add(c);return p;}
    Button Button(string label,Func<Task> action,string? display=null){var b=new Button{Content=display??label,IsEnabled=!historicalReference||label is not ("Guardar" or "Deshacer" or "Rehacer" or "Pegar rango" or "Registrar propuesta" or "Exportar revisión guardada" or "Opciones de celda")};Name(b,display??label);b.Click+=async(_,_)=>await Safe(action,label);return b;}
    Task Safe(Func<Task> action,string label="EDITOR_ACTION")=>AcceptsEditorWork?uiWork.Track(SafeCore(action,label)):Task.CompletedTask;
    async Task SafeCore(Func<Task> action,string label)
    {
        try{await OperationLifetime.RunAsync(label,"CSHARP_NATIVE_DETACHED_REVIEW",action);}
        catch(OperationCanceledException){state.Text="Operación cancelada; borrador conservado.";PilotLog.Event("SR1_EDITOR_OPERATION_CANCELLED",label);}
        catch(Exception e){buffer.Failed(e);state.Text="No guardado / borrador conservado: "+e.Message;PilotLog.Event("EDITOR_ACTION_ERROR",e.GetType().Name+";"+e.Message);}
    }
    void ShowSheet()
    {
        manualTargets=ManualCellReview.Targets(profile,sheet);
        interaction=WorksheetInteractionIndex.For(profile,sheet);
        editableCells=profile.Draft.Policies.Where(p=>p.Sheet==sheet&&MatrixReview.Editable(profile.Draft,sheet,p.Coordinate)).Select(p=>p.Coordinate).ToHashSet(StringComparer.Ordinal);
        grid=new(profile,sheet,source,CurrentCellValue,compact.IsChecked==true,fit.IsChecked==true,CurrentPresentation,CurrentMerges);
        grid.SetZoom(zoom);grid.Selected+=(coord,edit,extend)=>_=Safe(async()=>{if(buffer.Pending.Length>0&&buffer.Pending[0].Coordinate!=coord)await Save();Select(coord,edit);});
        grid.CellClicked+=(coord,point)=>{if(AcceptsEditorWork&&layer.SelectedIndex==0&&manualTargets.ContainsKey(coord))ShowManualOptions(coord,point);};
        viewport.Child=grid.View;renderedRevision=project.DisplayWork.Revision;setting=true;tabs.SelectedItem=sheet;setting=false;Select("A1",false);
        PilotLog.Event("SHEET_PHYSICAL_PROFILE",$"{sheet};Zoom={zoom};MDW={grid.MaximumDigitWidth};NoMinimumGeometryClamp=True");
        RefreshWorkingRowsView();
    }
    async Task ViewOptions()
    {
        if(grid==null)return;await Save();grid.SetView(compact.IsChecked==true,fit.IsChecked==true);Select(grid.Coordinate,false);
        PilotLog.Event("EDITOR_VIEW_OPTIONS",$"Compact={compact.IsChecked};Fit={fit.IsChecked};CollapsedRows={grid.CollapsedRows};SourceUnchanged=True");
    }
    string? CurrentCellValue(string coordinate)
    {
        if(historicalReference)return interaction?.Cells.GetValueOrDefault(coordinate)?.Value;
        // Keep the previous COMPLETE display snapshot responsive until the new snapshot is ready.
        // It is never used for CAS, writes or labour/economic decisions.
        if(displaySnapshot!=null&&(displayedRevision!=displaySnapshot.Revision||displayedSheet!=sheet))
        {
            displayedValues=profile.Draft.Sheets.Single(s=>s.Name==sheet).Cells.ToDictionary(c=>c.Coordinate,c=>displaySnapshot.Value(sheet,c.Coordinate,layer.SelectedIndex==0),StringComparer.Ordinal);
            displayedPresentation=manualTargets.Keys.Select(c=>(Key:MatrixReview.Key(sheet,c),Style:displaySnapshot.Presentation(sheet,c,layer.SelectedIndex==0))).Where(e=>e.Style!=null).ToDictionary(e=>e.Key,e=>e.Style!,StringComparer.Ordinal);
            displayedMerges=displaySnapshot.Merges(sheet,layer.SelectedIndex==0).ToArray();
            displayedRevision=displaySnapshot.Revision;displayedSheet=sheet;
        }
        return displayedValues.GetValueOrDefault(coordinate);
    }
    async Task PrepareDisplay()
    {
        var work=project.DisplayWork;var watch=Stopwatch.StartNew();
        var pendingTask=Task.Run(()=>CrossMatrixReview.For(work));
        var workingTask=historicalReference?Task.FromResult<WorkingEconomicRows?>(null):Task.Run<WorkingEconomicRows?>(()=>WorkingEconomicRows.For(work,profile));
        var readyTask=Task.Run(()=>EditorDisplaySnapshot.For(work,profile));
        // Observe and drain every parallel read even if one fails; no read continuation
        // may outlive its registered Safe/closing-save operation.
        await Task.WhenAll(pendingTask,workingTask,readyTask);
        var ready=await readyTask;
        var review=await pendingTask;
        var working=await workingTask;
        if(closed||!ReferenceEquals(work,project.DisplayWork))return;
        displaySnapshot=ready;reviewProjection=review;workingRows=working;RefreshEconomicReview();RefreshWorkingRowsView();PilotLog.Event("EDITOR_DISPLAY_PREPARED",$"Revision={ready.Revision}",watch.Elapsed.TotalMilliseconds);
    }
    ManualCellPresentation? CurrentPresentation(string coordinate){if(historicalReference)return null;_=CurrentCellValue(coordinate);if(IsTypedLate(coordinate)&&ManualCellReview.Current(project.DisplayWork,profile).GetValueOrDefault(MatrixReview.Key(sheet,coordinate)) is {} e)return TypedLatePresentation.For(e,true);return displayedPresentation.GetValueOrDefault(MatrixReview.Key(sheet,coordinate));}
    string[] CurrentMerges(){if(historicalReference)return profile.Draft.Sheets.Single(s=>s.Name==sheet).Merges;if(interaction!=null)_=CurrentCellValue("A1");return displayedMerges;}
    void Select(string coordinate,bool edit)
    {
        if(grid==null)return;var cell=interaction!.Cells.GetValueOrDefault(coordinate);setting=true;
        address.Text=coordinate;var writable=!historicalReference&&cell!=null&&editableCells.Contains(coordinate)&&project.DisplayWork.State!=SistemaMarcaciones.Monthly.Controlled.MonthlyState.CERRADO;
        content.IsReadOnly=!writable;content.Text=cell?.Formula!=null?"="+cell.Formula:cell==null?"":CurrentCellValue(coordinate)??"";
        var manual=layer.SelectedIndex==0&&manualTargets.ContainsKey(coordinate);
        cellDetail.Text=$"{interaction.Describe(grid.Range())} · {(writable?"Observación editable · F2 o doble clic":manual?"Opciones de revisión manual · NO ACREDITADA":"Protegida · Registrar propuesta")} · {Math.Round(zoom*100)}% · {grid.CollapsedRows} filas vacías compactadas solo en vista";setting=false;
        if(reviewProjection?.Related(profile.Draft.Id,sheet,coordinate).Length>0)cellDetail.Text+=" · CAMBIO PENDIENTE / IMPACTO ECONÓMICO POR DETERMINAR";
        if(edit&&writable){content.Focus(FocusState.Programmatic);content.SelectAll();}ShowState();
    }
    void ShowState()=>state.Text=historicalReference?"HISTORICAL_FINAL_MATRIX_READ_ONLY":buffer.Conflict?"CONFLICTO · borrador conservado; comparar antes de guardar":buffer.Pending.Length>0?"Cambios pendientes":"GUARDADO · revisión "+project.DisplayWork.Revision;
    async Task DocumentDetail()
    {
        if(WorkingRowsVisible){await WorkingRowsDetail();return;}
        if(grid==null)return;
        if(SpecificDocumentDetail is {} inspect&&await inspect(sheet,grid.Coordinate) is {} received)
        {
            var panel=new StackPanel{Spacing=12};panel.Children.Add(InstitutionalVisualTokens.Text(received.Summary,14));
            panel.Children.Add(new Expander{Header="Referencia técnica del documento capturado",Content=InstitutionalVisualTokens.Text(received.Technical,11),HorizontalContentAlignment=HorizontalAlignment.Stretch});
            await ShowTrackedDialog(new ContentDialog{XamlRoot=root.XamlRoot,Title="Documento exacto de la jornada",CloseButtonText="Cerrar",Content=new ScrollViewer{Content=panel,MaxHeight=560}});return;
        }
        var catalog=await coordinator.Documents();
        var result=DocumentConsultation.ForCell(catalog,profile.Draft,sheet,grid.Coordinate);
        var progress=ProgressiveDocuments.ForExactCell(catalog,profile.Draft,sheet,grid.Coordinate);
        var d=result.Current?.Data;
        var text=result.State+"\n"+string.Join("\n",result.Reasons);
        if(d!=null)text+=$"\nNúmero: {d.OriginalNumber}\n{d.Start:dd/MM/yyyy HH:mm} — {d.End:dd/MM/yyyy HH:mm}\n"+
            $"Aprobación: {d.ApprovalStatus}\nEntrega: {d.PhysicalDeliveryStatus}\nValidez: {d.DocumentValidityStatus}\n"+
            $"{d.Origin.SourceFileName} · {d.Origin.SheetOrPage}/{d.Origin.RowOrRecordLocator}\nSHA256: {d.Origin.SourceSHA256}\n"+
            string.Join("\n",result.History.Select(v=>$"Versión {v.Version}: {string.Join(", ",v.ChangedFields)}"));
        if(progress!=null)text+=$"\nCompletitud v{progress.Version}: {progress.Temporal.Kind}\n"+string.Join("\n",progress.Completion.MissingFields)+
            $"\nTranscripciones pendientes: {progress.Transcriptions.Length}; conflictos: {progress.Conflicts.Length}. NO APLICADO.";
        var dialog=new ContentDialog{Title="Documento relacionado · candidato, no aplicado",Content=new ScrollViewer{Content=InstitutionalVisualTokens.Text(text,12),MaxHeight=540},
            PrimaryButtonText=progress==null?"":"Abrir módulo documental",CloseButtonText="Cerrar",XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme};
        if(await ShowTrackedDialog(dialog)==ContentDialogResult.Primary&&progress!=null)OpenDocumentModule?.Invoke(progress.Data.Module);
    }
    public event Action<DocumentModule>? OpenDocumentModule;
    async Task Save()
    {
        if(historicalReference)return;
        if(buffer.Pending.Length==0)return;await serial.WaitAsync();try
        {
            if(buffer.Pending.Length==0)return; // Another focus/selection save may have committed while awaiting the same serial gate.
            if(buffer.Conflict)throw new InvalidOperationException("CAS_DRAFT_REQUIRES_EXPLICIT_COMPARISON");state.Text="Guardando…";
            var expected=buffer.Revision;var pending=buffer.Pending;var why=reason.Text;
            using(OperationLifetime.EnterCommit())await Task.Run(()=>coordinator.Save(project,profile.Draft.Id,expected,pending,why,"F13_SANDBOX_SESSION",DateTimeOffset.UtcNow));
            buffer.Saved(project.DisplayWork.Revision);await PrepareDisplay();RefreshCommitted();ShowState();PilotLog.Event("EDITOR_SAVED",$"Revision={project.DisplayWork.Revision}");
        }finally{serial.Release();}
    }
    async Task Move(int r,int c,bool extend){await Save();grid!.Move(r,c,extend);}
    async Task History(bool redo)
    {
        HistoricalFinalMatrices.RequireWorking(profile.Draft);
        await Save();using var commit=OperationLifetime.EnterCommit();if(layer.SelectedIndex==0&&ManualCellReview.Events(project.DisplayWork,profile.Draft.Id).Length>0)
            await Task.Run(()=>coordinator.ManualRangeHistory(project,profile,redo,"F13_SANDBOX_SESSION",DateTimeOffset.UtcNow));
        else await Task.Run(()=>coordinator.History(project,profile.Draft.Id,redo,"F13_SANDBOX_SESSION",DateTimeOffset.UtcNow));
        await PrepareDisplay();RefreshCommitted();Select(grid!.Coordinate,false);
    }
    Task CellOptions()
    {
        if(grid==null)return Task.CompletedTask;
        if(editableCells.Contains(grid.Coordinate))Select(grid.Coordinate,true);
        else if(layer.SelectedIndex==0&&manualTargets.ContainsKey(grid.Coordinate))ShowManualOptions(grid.Coordinate,new Windows.Foundation.Point(80,40));
        else state.Text="Esta celda está protegida: sin revisión manual. Usa Registrar propuesta; no se aplica automáticamente.";
        return Task.CompletedTask;
    }
    void ShowManualOptions(string coordinate,Windows.Foundation.Point point)
    {
        if(historicalReference||!AcceptsEditorWork)return;
        if(grid==null||manualDialog||activeOptions!=null||project.DisplayWork.State==SistemaMarcaciones.Monthly.Controlled.MonthlyState.CERRADO)return;
        var selectedSheet=sheet;var selectedCoordinates=grid.Range();var selectedIndex=interaction!;var menu=new MenuFlyout();
        try{_=selectedIndex.Resolve(selectedCoordinates);}catch(InvalidOperationException){state.Text="El rango incluye celdas protegidas. Selecciona solo novedades de persona/fecha.";return;}
        var heading=new MenuFlyoutItem{Text=selectedIndex.Describe(selectedCoordinates),IsEnabled=false};menu.Items.Add(heading);
        Task ConfirmManual(string s,string c,ManualChoice choice)=>ConfirmManualRange(s,c,selectedCoordinates,choice);
        foreach(var choice in ManualCellReview.Choices)
        {
            var item=new MenuFlyoutItem{Text=choice.Label};Name(item,$"{coordinate}: revisión manual {choice.Label}, no acreditada");
            item.Click+=async(_,_)=>await Safe(()=>ConfirmManual(selectedSheet,coordinate,choice));menu.Items.Add(item);
        }
        var late=new ManualChoice("TYPED_LATENESS","Atraso (minutos manuales)",null);
        var lateItem=new MenuFlyoutItem{Text=late.Label};lateItem.Click+=async(_,_)=>await Safe(()=>ConfirmManual(selectedSheet,coordinate,late));menu.Items.Add(lateItem);
        activeOptions=menu;menu.Closed+=(_,_)=>{if(ReferenceEquals(activeOptions,menu))activeOptions=null;};
        try{menu.ShowAt(grid.Root,new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions{Position=point});}
        catch{activeOptions=null;throw;}
        PilotLog.Event("EDITOR_RANGE_OPTIONS_OPENED",$"Cells={selectedCoordinates.Length};Revision={project.DisplayWork.Revision}");
    }
    async Task ConfirmManualRange(string selectedSheet,string coordinate,string[] selectedCoordinates,ManualChoice choice)
    {
        if(manualDialog)return;manualDialog=true;try
        {
            await Save();var expected=project.DisplayWork.Revision;
            var original=MatrixReview.Value(project.DisplayWork,profile.Draft,selectedSheet,coordinate);
            var current=ManualCellReview.Value(project.DisplayWork,profile,selectedSheet,coordinate);
            bool typedLate=choice.Id=="TYPED_LATENESS";
            var text=new TextBox{Header=typedLate?"Minutos enteros de atraso por cada fecha seleccionada (1–1440), NO acreditados":"Texto de revisión manual",Text=typedLate?"":choice.Value??current??"",MaxLength=32767,IsReadOnly=choice.Value!=null};
            var why=new TextBox{Header="Motivo obligatorio (sandbox)",MaxLength=4000,AcceptsReturn=true,Text=""};Name(text,"Valor manual separado del resultado original");Name(why,"Motivo obligatorio de revisión manual");
            var index=WorksheetInteractionIndex.For(profile,selectedSheet);string? combined=null;
            try{combined=ManualRangeReview.Combine(profile,selectedSheet,selectedCoordinates);}catch(InvalidOperationException){ /* Multiple people can be reviewed together but never merged. */ }
            var combine=new CheckBox{Content="Combinar el período en la revisión manual (solo las fechas representadas)",IsEnabled=combined!=null,IsChecked=CurrentMerges().Any(m=>m==combined)};Name(combine,"Combinar intervalo de un mismo servidor solo en sandbox");
            var panel=new StackPanel{Spacing=8};panel.Children.Add(InstitutionalVisualTokens.Text($"{selectedSheet} · {index.Describe(selectedCoordinates)}\nCelda activa: {coordinate}\nOriginal: {original??"(sin dato)"}\nRevisión actual: {current??"(sin dato)"}\n{index.Resolve(selectedCoordinates).Length} celda(s) de resultado. Shift + clic, Shift + flechas o arrastrar para seleccionar un rango.\nNO ACREDITADA. No aprueba documentos ni justifica la jornada. Totales y valores económicos NO recalculados.",12));panel.Children.Add(text);panel.Children.Add(combine);panel.Children.Add(why);
            var dialog=new ContentDialog{XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,Title="Confirmar revisión manual separada",Content=panel,PrimaryButtonText="Guardar revisión manual",CloseButtonText="Cancelar"};
            dialog.PrimaryButtonClick+=(_,e)=>{if(string.IsNullOrWhiteSpace(why.Text)||string.IsNullOrWhiteSpace(text.Text)||typedLate&&(!decimal.TryParse(text.Text,System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out var minutes)||minutes<1||minutes>1440)){e.Cancel=true;why.PlaceholderText="Indica motivo y, para atraso, minutos enteros entre 1 y 1440.";why.Focus(FocusState.Programmatic);}};
            if(await ShowTrackedDialog(dialog)!=ContentDialogResult.Primary)return;
            var requested=text.Text;var reasonText=why.Text;bool combinePeriod=combine.IsChecked==true;
            // Legacy SaveManual(project,profile,expected,...) is preserved; the shared UI uses an atomic V2 range.
            var saveWatch=Stopwatch.StartNew();state.Text="Guardando revisión manual…";
            using var commit=OperationLifetime.EnterCommit();
            if(typedLate)await Task.Run(()=>TypedLateCommands.Save(coordinator,project,profile,expected,selectedSheet,selectedCoordinates,decimal.Parse(requested,System.Globalization.CultureInfo.InvariantCulture),combinePeriod,reasonText,"F13_SANDBOX_SESSION",DateTimeOffset.UtcNow));
            else await Task.Run(()=>coordinator.SaveManualRange(project,profile,expected,selectedSheet,selectedCoordinates,choice.Id,requested,combinePeriod,reasonText,"F13_SANDBOX_SESSION",DateTimeOffset.UtcNow));
            PilotLog.Event("EDITOR_MANUAL_COMMIT_COMPLETED",$"Cells={selectedCoordinates.Length};Revision={project.DisplayWork.Revision}",saveWatch.Elapsed.TotalMilliseconds);
            await PrepareDisplay();RefreshCommitted();if(grid!=null)Select(grid.Coordinate,false);state.Text="Revisión manual guardada / NO ACREDITADA · revisión "+project.DisplayWork.Revision;
            PilotLog.Event("EDITOR_MANUAL_REVIEW_SAVED",$"{selectedSheet}!{coordinate};Choice={choice.Id};Revision={project.DisplayWork.Revision};LabourApplied=False");
        }finally{manualDialog=false;}
    }
    Task Copy()
    {var data=new DataPackage();data.SetText(WorkingRowsVisible?WorkingRowsClipboard():grid!.CopyText());Clipboard.SetContent(data);state.Text="Rango copiado. Sin escritura de workspace.";return Task.CompletedTask;}
    async Task Paste()
    {
        if(WorkingRowsVisible){state.Text="Edita la novedad persona/fecha en su matriz de origen; esta tabla es una proyección de revisión.";return;}
        HistoricalFinalMatrices.RequireWorking(profile.Draft);
        await Save();var clipboard=Clipboard.GetContent();if(!clipboard.Contains(StandardDataFormats.Text))return;var raw=await clipboard.GetTextAsync();
        var start=MatrixCoordinates.Parse(grid!.Coordinate);var rows=raw.Replace("\r\n","\n",StringComparison.Ordinal).TrimEnd('\n').Split('\n');var changes=new List<CellChange>();
        for(int r=0;r<rows.Length;r++){var values=rows[r].Split('\t');for(int c=0;c<values.Length;c++)changes.Add(new(sheet,MatrixCoordinates.A1(start.Row+r,start.Column+c),values[c]));}
        ProjectCoordinator.ValidateRange(project.DisplayWork,profile.Draft.Id,changes);buffer.Begin(project.DisplayWork.Revision,changes);await Save();
    }
    async Task Proposal()
    {
        if(WorkingRowsVisible){state.Text="Registra la propuesta en la celda persona/fecha de origen, no en una fila virtual.";return;}
        HistoricalFinalMatrices.RequireWorking(profile.Draft);
        if(grid==null)return;var requested=new TextBox{Header="Cambio solicitado (NO aprobado ni aplicado)",Text=content.Text,MaxLength=32767};
        var dialog=new ContentDialog{XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,Title="Propuesta de corrección",Content=requested,PrimaryButtonText="Registrar propuesta",CloseButtonText="Cancelar"};
        if(await ShowTrackedDialog(dialog)==ContentDialogResult.Primary){using var commit=OperationLifetime.EnterCommit();await coordinator.Propose(project,profile.Draft.Id,new(sheet,grid.Coordinate,requested.Text),reason.Text,"F13_SANDBOX_SESSION",DateTimeOffset.UtcNow);}
    }
    async Task Compare()
    {
        if(!buffer.Conflict||buffer.Pending.Length==0)return;await coordinator.Refresh(project);var revision=project.DisplayWork.Revision;var pending=buffer.Pending;
        var text=string.Join("\n",pending.Select(p=>$"{p.Sheet}!{p.Coordinate}\nGuardado: {MatrixReview.Value(project.DisplayWork,profile.Draft,p.Sheet,p.Coordinate)}\nBorrador local: {p.Value}"));
        var dialog=new ContentDialog{XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme,Title="Comparar revisión "+revision,
            Content=new ScrollViewer{Content=InstitutionalVisualTokens.Text(text,12)},PrimaryButtonText="Conservar y guardar borrador",SecondaryButtonText="Descartar borrador",CloseButtonText="Seguir comparando"};
        var answer=await ShowTrackedDialog(dialog);if(answer==ContentDialogResult.Primary){buffer.Begin(revision,pending);await Save();}
        else if(answer==ContentDialogResult.Secondary){buffer.DiscardExplicitly(project.DisplayWork.Revision);Select(grid!.Coordinate,false);}
    }
    async Task Export()
    {
        HistoricalFinalMatrices.RequireWorking(profile.Draft);
        if(ProductActionCheck is {} check)await check();else await NativeActionAdmission.FromEnvironment();
        using var commit=OperationLifetime.EnterCommit();
        if(SpecificPopulationExport is {} populationExport)
        {
            await Save();state.Text=await populationExport();PilotLog.Event("POPULATION_EDITOR_EXPORT_DURABLY_CONFIRMED",state.Text);return;
        }
        if(layer.SelectedIndex==0&&SistemaMarcaciones.Monthly.Projects.ProjectEvents.Read<NativeMatrixRegistration>(project.Work,NativeAttendanceCoordinator.Contract).Any(r=>r.MatrixId==profile.Draft.Id))
        {
            await Save();var consolidated=await Task.Run(()=>CompletionExports.Export(coordinator,project,profile,source,destination,project.Work.Revision,"F13_SANDBOX_SESSION",DateTimeOffset.UtcNow));
            state.Text=$"Exportación integrada confirmada · revisión {consolidated.ConfirmedRevision}. {consolidated.WorkingRows} filas de trabajo. Sin cálculo económico.";PilotLog.Event("COMPLETION_EXPORT_DURABLY_CONFIRMED",System.Text.Json.JsonSerializer.Serialize(consolidated));return;
        }
        if(layer.SelectedIndex==0&&ManualCellReview.Events(project.DisplayWork,profile.Draft.Id).Length>0)
        {
            // Existing ExportManual(project,profile,...) V1 remains byte/style preserving. V2 explicitly exports the manual F2 presentation.
            await Save();var manual=await Task.Run(()=>coordinator.ExportManualPresentation(project,profile,source,destination,"F13_SANDBOX_SESSION",DateTimeOffset.UtcNow));
            var workingExport=await Task.Run(()=>WorkingRowsExport.Export(coordinator,project,profile,Path.Combine(destination,manual.FileName),manual.SHA256,destination,"F13_SANDBOX_SESSION",DateTimeOffset.UtcNow));
            state.Text=$"Copia NO ACREDITADA con {workingExport.WorkingRows} filas de trabajo; originales y fórmulas conservados en sus hojas. Sin cálculo económico.";PilotLog.Event("EDITOR_MANUAL_EXPORT",manual.FileName);PilotLog.Event("WORKING_ROWS_EXPORT",workingExport.FileName);return;
        }
        await Save();var receipt=await coordinator.Export(project,profile.Draft.Id,source,destination,"F13_SANDBOX_SESSION",DateTimeOffset.UtcNow);
        var pending=CrossMatrixReview.For(project.DisplayWork).Matrix(profile.Draft.Id).Length;
        state.Text=$"Copia exportada desde revisión {receipt.Revision}. No certificada."+(pending>0?$" {pending} cambios pendientes en evidencia separada; importes originales conservados.":"");PilotLog.Event("EDITOR_EXPORT",receipt.FileName);
    }
    Task Search()
    {
        var matches=profile.Draft.Sheets.Single(s=>s.Name==sheet).Cells.Where(c=>CurrentCellValue(c.Coordinate)?.Contains(find.Text,StringComparison.OrdinalIgnoreCase)==true).ToArray();
        var current=Array.FindIndex(matches,c=>c.Coordinate==grid!.Coordinate);if(matches.Length>0){var next=matches[(current+1)%matches.Length];grid!.Select(next.Coordinate);grid.Reveal(next.Coordinate);}return Task.CompletedTask;
    }
    Task Zoom(double delta){zoom=Math.Clamp(zoom+delta,.25,2);grid!.SetZoom(zoom);Select(grid.Coordinate,false);return Task.CompletedTask;}
    void OnCanonicalChange(CanonicalChange change)
    {
        if(change.ProjectId!=project.Id||!AcceptsEditorWork)return;DispatcherQueue.TryEnqueue(async()=>await Safe(async()=>
        {
            if(closed)return;await PrepareDisplay();if(closed)return;
            buffer.Observe(change.Revision);RefreshCommitted();if(buffer.Pending.Length==0&&grid!=null)Select(grid.Coordinate,false);
            if(change.Kind=="DOCUMENT_DELIVERY_CHANGED")cellDetail.Text="Estado documental actualizado desde el catálogo compartido. Resultados laborales intactos.";ShowState();
        }));
    }
    void RefreshCommitted()
    {if(grid==null||displaySnapshot==null||renderedRevision==displaySnapshot.Revision)return;var watch=Stopwatch.StartNew();renderedRevision=displaySnapshot.Revision;grid.Refresh();PilotLog.Event("EDITOR_VIEW_REFRESH",$"Revision={renderedRevision}",watch.Elapsed.TotalMilliseconds);}
    void RefreshEconomicReview()
    {
        if(reviewProjection==null)return;
        var watch=Stopwatch.StartNew();var rows=reviewProjection.Matrix(profile.Draft.Id);
        pendingEconomic.Header=$"Cambios pendientes · {rows.Length} fecha(s) · revisión {reviewProjection.Revision}";
        var table=new InstitutionalTable("Cambios de novedad pendientes de acreditación. Valores originales conservados.");
        table.Configure(new("Servidor QA",120),new("Fecha",100),new("Original",160),new("Cambio pendiente",170),new("Estado económico",270));
        table.Rows(rows.Select(p=>new PresentationRow(p.ReviewId+":"+p.Date,
            [p.AnonymousLabel,p.Date.ToString("dd/MM/yyyy"),p.Original??"Sin dato",p.Manual??"Sin dato",p.EconomicImpact],p)));
        pendingEconomic.Content=rows.Length==0?InstitutionalVisualTokens.Text("Sin cambios manuales pendientes. Valores económicos originales conservados según su evidencia.",12):table.Root;
        PilotLog.Event("CROSS_MATRIX_REVIEW_VIEW_UPDATED",$"Revision={reviewProjection.Revision};PendingDates={rows.Length};EconomicAltered=False",watch.Elapsed.TotalMilliseconds);
    }
    void LogSaveProfile(SaveMeasurement measurement)=>PilotLog.Event("EDITOR_SAVE_PROFILE",System.Text.Json.JsonSerializer.Serialize(measurement),measurement.TotalMilliseconds);
}
