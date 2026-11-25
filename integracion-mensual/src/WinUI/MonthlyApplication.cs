using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;
using SistemaMarcaciones.F13.Integrated.Native;
using SistemaMarcaciones.F13.Operational;
using SistemaMarcaciones.Monthly.Controlled;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Monthly.Projects;
using SistemaMarcaciones.Monthly.Export.Verified;
using Windows.Storage.Pickers;

namespace SistemaMarcaciones.Desktop.Pilot.WinUI;

internal sealed record LocalApplicationConfiguration(string Identity,string DataRoot,string BoundaryRoot,
    string LaunchPolicy,string LaunchPolicySHA256,string? ReviewPolicy,string? ReviewPolicySHA256,bool AutomatedQa,
    string[]? AdmittedQaSources=null);

internal static class MonthlyProgram
{
    [STAThread] public static void Main()
    {
        var arguments=Environment.GetCommandLineArgs().Skip(1).ToArray();
        if(arguments.Length>0&&!arguments.SequenceEqual(new[]{"--qa-local"}))throw new InvalidDataException("Parámetros de inicio no admitidos.");
        var configPath=Path.Combine(AppContext.BaseDirectory,arguments.Length==0?"CBA.local.json":"CBA.qa.json");
        var config=JsonSerializer.Deserialize<LocalApplicationConfiguration>(File.ReadAllText(configPath))
            ??throw new InvalidDataException("Falta la configuración local de CBA.");
        if(config.Identity!="CBA-MENSUAL-0.8.1.400-LOCAL")throw new InvalidDataException("Configuración de otra versión.");
        var root=Path.GetFullPath(config.DataRoot);
        var normal=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CBA","MarcacionesWinUI")+Path.DirectorySeparatorChar;
        var qa=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"CBA_QA","TEST_FIXTURE_ONLY")+Path.DirectorySeparatorChar;
        if(config.AutomatedQa){if(!root.StartsWith(qa,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("La prueba debe estar separada de los datos normales.");if(config.AdmittedQaSources is not {Length:>0}||config.AdmittedQaSources.Any(h=>h.Length!=64||!h.All(Uri.IsHexDigit)))throw new InvalidDataException("Falta la relación exacta de archivos admitidos para esta prueba.");OwnedLaunchBoundary.Initialize(config.BoundaryRoot,config.LaunchPolicy,config.LaunchPolicySHA256);}
        else{if(!root.StartsWith(normal,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("La carpeta de datos debe ser independiente de la aplicación anterior.");OwnedLaunchBoundary.InitializeLocal(config.BoundaryRoot,config.LaunchPolicy,config.LaunchPolicySHA256);}
        PilotLog.Root=Path.Combine(config.BoundaryRoot,"ui");Directory.CreateDirectory(PilotLog.Root);
        AppDomain.CurrentDomain.UnhandledException+=(_,e)=>PilotLog.Event("MONTHLY_UNHANDLED",e.ExceptionObject.ToString()??"");
        TaskScheduler.UnobservedTaskException+=(_,e)=>PilotLog.Event("MONTHLY_UNOBSERVED",e.Exception.ToString());
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback=>{SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));_=new MonthlyApplication(config);});
    }
}

internal sealed class MonthlyApplication(LocalApplicationConfiguration configuration):Application,IXamlMetadataProvider
{
    readonly IXamlMetadataProvider provider=new XamlControlsXamlMetaDataProvider();Window? window;
    public IXamlType GetXamlType(Type t)=>provider.GetXamlType(t);
    public IXamlType GetXamlType(string n)=>provider.GetXamlType(n);
    public XmlnsDefinition[] GetXmlnsDefinitions()=>provider.GetXmlnsDefinitions();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());InstitutionalVisualTokens.Install();
        var authority=configuration.ReviewPolicy==null?null:new LocalSourceReviewAuthority(configuration.ReviewPolicy,configuration.ReviewPolicySHA256??"");
        var session=new OperationalSession(configuration.DataRoot,configuration.AutomatedQa?"QA_VISUAL_AUTOMATED":Environment.UserName,authority,configuration.AutomatedQa);
        window=new MonthlyWindow(session,configuration);window.Activate();
        try{await OperationLifetime.RunAsync("Abrir aplicación","CSHARP_MONTHLY_LOCAL",async()=>{await session.Initialize(OperationLifetime.CurrentToken);await ((MonthlyWindow)window).Refresh();});PilotLog.Event("MONTHLY_EMPTY_START_READY");}
        catch(OperationCanceledException){PilotLog.Event("MONTHLY_STARTUP_CANCELLED");}
        catch(Exception error){PilotLog.Event("MONTHLY_STARTUP_FAILED",error.ToString());if(!((MonthlyWindow)window).Closing)await ((MonthlyWindow)window).ShowError(error);}
    }
}

internal sealed class MonthlyWindow:Window
{
    readonly OperationalSession session;
    readonly LocalApplicationConfiguration configuration;
    readonly Grid root=new(){ColumnSpacing=0,RequestedTheme=ElementTheme.Light};
    readonly StackPanel navigation=new(){Spacing=3,Padding=new(12)};
    readonly Grid main=new(){Padding=new(24,20,24,14),RowSpacing=12};
    readonly StackPanel heading=new(){Spacing=6};
    readonly ContentControl actions=new(){HorizontalContentAlignment=HorizontalAlignment.Stretch};
    readonly Grid body=new();
    readonly TextBlock status=Text("Todo se guarda en esta copia local independiente.",12);
    readonly List<DetachedExcelWindow> editors=[];
    readonly UiWorkSet work=new();
    readonly Dictionary<string,Button> menu=[];
    string page="Inicio";
    bool busy,closing,closePermitted;
    Task? closingTask;
    internal bool Closing=>closing;
    public MonthlyWindow(OperationalSession active,LocalApplicationConfiguration config)
    {
        session=active;configuration=config;Title="Sistema de Marcaciones CBA · Integración mensual 0.8.1.400";
        root.ColumnDefinitions.Add(new(){Width=new(222)});root.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
        InstitutionalVisualTokens.Brush(root,Panel.BackgroundProperty,"SurfacePrimary");
        InstitutionalVisualTokens.Brush(navigation,Panel.BackgroundProperty,"InstitutionalNavy");
        navigation.Children.Add(InstitutionalVisualTokens.Text("CBA\nTALENTO HUMANO",19,true,"InstitutionalWhite"));
        foreach(var name in new[]{"Inicio","Matrices de novedades","Marcaciones","Permisos","Comisiones","Licencias","Vacaciones","Justificaciones","Feriados","Novedades y Centro","Régimen disciplinario","Memorandos","Configuración"})
        {
            var button=new Button{Content=name,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left,MinHeight=38,Style=(Style)Application.Current.Resources["InstitutionalSidebarButton"]};
            button.Click+=async(_,_)=>{if(closing||busy)return;await Run("Abrir "+name,()=>{page=name;return Task.CompletedTask;});};
            menu[name]=button;navigation.Children.Add(button);
        }
        navigation.Children.Add(InstitutionalVisualTokens.Text("Copia local de revisión\nSin conexión automática a datos oficiales",11,false,"SidebarMuted"));
        root.Children.Add(new ScrollViewer{Content=navigation,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})main.RowDefinitions.Add(new(){Height=height});
        main.Children.Add(heading);Grid.SetRow(actions,1);main.Children.Add(actions);Grid.SetRow(body,2);main.Children.Add(body);Grid.SetRow(status,3);main.Children.Add(status);Grid.SetColumn(main,1);root.Children.Add(main);Content=root;
        if(AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)p.Maximize();
        AppWindow.Closing+=(sender,args)=>{if(closePermitted)return;args.Cancel=true;_=CloseSafely();};
    }
    static TextBlock Text(string value,int size=14,bool bold=false)=>InstitutionalVisualTokens.Text(value,size,bold);
    static StackPanel Column(params UIElement[] items){var p=new StackPanel{Spacing=12};foreach(var item in items)p.Children.Add(item);return p;}
    static PilotWrapPanel Line(params UIElement[] items){var p=new PilotWrapPanel();foreach(var item in items)p.Children.Add(item);return p;}
    Button Button(string label,Func<Task> action)
    {var b=new Button{Content=label,MinHeight=38};b.Click+=async(_,_)=>await Run(label,action);return b;}
    Task Run(string label,Func<Task> action)=>closing?Task.CompletedTask:work.Track(RunCore(label,action));
    async Task RunCore(string label,Func<Task> action)
    {
        if(busy)return;busy=true;status.Text=label+"…";
        foreach(var b in menu.Values)b.IsEnabled=false;actions.IsEnabled=false;
        try{await OperationLifetime.RunAsync(label,"CSHARP_MONTHLY_LOCAL",action);status.Text="Guardado. "+label+" completado.";if(!closing)await Refresh();}
        catch(OperationCanceledException){status.Text="Operación cancelada. Los guardados ya confirmados se conservan; consulta el estado del trabajo.";}
        catch(Exception error){PilotLog.Event("MONTHLY_ACTION_ERROR",error.ToString());status.Text=error.Message;if(!closing)await ShowError(error);}
        finally{busy=false;if(!closing){foreach(var b in menu.Values)b.IsEnabled=true;actions.IsEnabled=true;}}
    }
    public async Task ShowError(Exception error)
    {
        var technical=System.Text.RegularExpressions.Regex.IsMatch(error.Message,"^[A-Z0-9_ :.-]+$");
        var message=technical?"No se pudo validar la operación con la información disponible. Comprueba el archivo, su período y los datos requeridos; no se ha confirmado un resultado parcial.":error.Message;
        await Dialog("No se pudo completar",Column(Text(message),new Expander{Header="Detalle para soporte",Content=Text(error.Message,12),HorizontalContentAlignment=HorizontalAlignment.Stretch}),"Entendido");
    }
    async Task<ContentDialogResult> Dialog(string title,UIElement content,string primary="Aceptar",string? secondary=null)
    {
        var d=new ContentDialog{Title=title,Content=content,PrimaryButtonText=primary,CloseButtonText=secondary??"",DefaultButton=ContentDialogButton.Primary,XamlRoot=root.XamlRoot,MaxWidth=1000,RequestedTheme=ElementTheme.Light,PrimaryButtonStyle=(Style)Application.Current.Resources["InstitutionalPrimaryButton"]};
        return await d.ShowAsync();
    }
    async Task<string?> Pick(params string[] extensions)
    {
        var picker=new FileOpenPicker();foreach(var ext in extensions)picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));
        var selected=(await picker.PickSingleFileAsync().AsTask(OperationLifetime.CurrentToken))?.Path;
        if(selected!=null&&configuration.AutomatedQa&&!configuration.AdmittedQaSources!.Contains(PhysicalMatrix.Hash(selected),StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Este archivo no pertenece a las fuentes anónimas admitidas para la prueba. No se ha importado.");
        return selected;
    }
    void RequireProject(){if(session.Handle==null)throw new InvalidOperationException("Crea o abre primero un trabajo mensual en «Matrices de novedades».");}
    string Person(string id)=>session.People.SingleOrDefault(p=>p.Id==id)?.Name??"Identidad pendiente";
    public async Task Refresh()
    {
        if(closing)return;
        heading.Children.Clear();body.Children.Clear();actions.Content=null;
        heading.Children.Add(Text(page,25,true));
        heading.Children.Add(Text(session.Handle==null?"Selecciona un período o crea un trabajo mensual.":$"{session.Period.Start:dd/MM/yyyy} — {session.Period.End:dd/MM/yyyy} · {session.People.Length} servidores · revisión {session.Handle.Work.Revision}",13));
        foreach(var (name,b) in menu)InstitutionalVisualTokens.Brush(b,Control.BackgroundProperty,name==page?"InstitutionalRed":"InstitutionalNavy");
        if(page is "Inicio" or "Matrices de novedades"){await ProjectsPage();return;}
        if(session.Handle==null){body.Children.Add(Text("Abre «Matrices de novedades» para comenzar."));return;}
        if(page=="Configuración"){ConfigurationPage();return;}
        if(page=="Marcaciones"){MarksPage();return;}
        if(page=="Novedades y Centro"){CasesPage();return;}
        if(page=="Memorandos"){MemosPage();return;}
        if(page=="Régimen disciplinario"){await DisciplinePage();return;}
        if(page=="Feriados"){HolidaysPage();return;}
        await DocumentsPage();
    }
    async Task ProjectsPage()
    {
        var month=new ComboBox{Width=180,ItemsSource=CultureInfo.GetCultureInfo("es-EC").DateTimeFormat.MonthNames.Take(12).ToArray(),SelectedIndex=DateTime.Today.Month-1};
        var year=new NumberBox{Value=DateTime.Today.Year,Minimum=2000,Maximum=2100,Width=112,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact};
        var buttons=Line(month,year,Button("Crear trabajo mensual",async()=>{if(editors.Count>0)throw new InvalidOperationException("Guarda y cierra las matrices abiertas antes de cambiar de período.");if(double.IsNaN(year.Value)||year.Value!=Math.Truncate(year.Value)||month.SelectedIndex<0)throw new InvalidOperationException("Selecciona un mes y un año entero.");await session.Create((int)year.Value,month.SelectedIndex+1);page="Configuración";}));
        if(session.Handle!=null)
        {
            buttons.Children.Add(Button("Procesar el mes",async()=>{RequireProject();await OperationalNative.Process(session,OperationLifetime.CurrentToken);page="Novedades y Centro";}));
            buttons.Children.Add(Button("Generar matrices",async()=>{var token=OperationLifetime.CurrentToken;await Task.Run(()=>OperationalArtifacts.Generate(session,token),token);}));
            buttons.Children.Add(Button("Abrir matriz",OpenMatrix));
            buttons.Children.Add(Button("Exportar Excel",async()=>{var token=OperationLifetime.CurrentToken;var output=await Task.Run(()=>ExportsWithNames(token),token);await SaveCopies(output.Files,output.Names);}));
        }
        actions.Content=buttons;
        var summaries=await session.Summaries();
        var table=Table("Trabajos mensuales",[new("Período",300),new("Estado",180),new("Revisión",100,true)],summaries.Select(s=>new PresentationRow(s.Project.Id,[s.Project.Name,"Guardado",s.WorkspaceRevision.ToString()],s)).ToArray(),false);
        table.Open+=r=>_=Run("Abrir trabajo",async()=>{if(editors.Count>0)throw new InvalidOperationException("Guarda y cierra las matrices abiertas antes de cambiar de período.");await session.Open(r.Key);page="Matrices de novedades";});
        body.Children.Add(table.Root);
        if(summaries.Length==0)status.Text="No hay ejemplos precargados. Crea el mes, carga personal y jornadas, e importa tus fuentes.";
    }
    InstitutionalTable Table(string name,PresentationColumn[] columns,PresentationRow[] rows,bool detail=true)
    {
        var t=new InstitutionalTable(name);t.Configure(columns);t.Rows(rows);
        if(detail)t.Open+=row=>_=Run("Consultar detalle",()=>ShowDetail(name,row));
        int lastColumn=-1;bool descending=false;
        t.Sort+=column=>
        {
            descending=lastColumn==column&&!descending;lastColumn=column;
            var ordered=columns[column].Numeric?rows.OrderBy(r=>decimal.TryParse(r.Cells[column],out var n)?n:decimal.MinValue).ToArray():rows.OrderBy(r=>r.Cells[column],StringComparer.CurrentCultureIgnoreCase).ToArray();
            t.Rows(descending?ordered.Reverse():ordered);t.SortIndicator(column,descending);
        };
        return t;
    }
    async Task ShowDetail(string title,PresentationRow row)
    {
        var p=new StackPanel{Spacing=10,MinWidth=560};
        foreach(var c in row.Cells)p.Children.Add(Text(c));
        var raw=new TextBox{Text=JsonSerializer.Serialize(row.Source,new JsonSerializerOptions{WriteIndented=true}),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap};
        p.Children.Add(new Expander{Header="Evidencia y referencia de origen",Content=raw,HorizontalContentAlignment=HorizontalAlignment.Stretch});
        await Dialog(title,new ScrollViewer{Content=p,MaxHeight=560},"Cerrar");
    }
    void ConfigurationPage()
    {
        actions.Content=Line(Button("Cargar personal",async()=>{var p=await Pick(".csv");if(p!=null)await session.ImportPeople(p,OperationLifetime.CurrentToken);}),
            Button("Cargar jornadas documentadas",async()=>{var p=await Pick(".csv");if(p!=null)await session.ImportDuties(p,OperationLifetime.CurrentToken);}),
            Button("Guardar formatos de carga",async()=>{await OperationalArtifacts.WriteImportTemplates(session.Root);await SaveCopies(Directory.GetFiles(Path.Combine(session.Root,"formatos_de_carga")));}),Button("Verificar alcance de fuentes",ReviewSources));
        body.Children.Add(Table("Personal del período",[new("Servidor",260),new("Población",260),new("Cargo",260),new("Vigencia",210)],
            session.People.OrderBy(p=>p.Name,StringComparer.CurrentCultureIgnoreCase).Select(p=>new PresentationRow(p.Id,[p.Name,OperationalLabels.Population(p.Population),p.Position,$"{p.From:dd/MM/yyyy} — {p.Until:dd/MM/yyyy}"],p)).ToArray()).Root);
    }
    async Task ReviewSources()
    {
        RequireProject();var sourceSet=await session.SourceSet();
        var reference=new TextBox{Header="Referencia de la revisión de TI / Talento Humano"};
        var devices=new TextBox{Header="Dispositivos incluidos"};
        var filters=new TextBox{Header="Filtros usados para generar y buscar las fuentes"};
        var until=new CalendarDatePicker{Header="Cobertura hasta (incluye D+1 de Guardia)",Date=new DateTimeOffset(session.Period.End.ToDateTime(TimeOnly.MinValue))};
        var bio=new CheckBox{Content="Comprobé la cobertura completa de las marcaciones de estas fuentes."};
        var docs=new CheckBox{Content="Comprobé la búsqueda completa de permisos, comisiones, cursos, justificativos, licencias y vacaciones."};
        if(await Dialog("Verificación expresa de las fuentes",new ScrollViewer{Content=Column(Text("Esta declaración se vincula a las fuentes exactas actuales. La importación por sí sola no acredita su integridad ni autoriza resultados laborales oficiales."),Text($"Alcance: {session.Period.Start:dd/MM/yyyy} a {session.Period.End:dd/MM/yyyy}. {session.People.Length} servidores. {session.Host.Sources.Length} archivos biométricos y {(await session.Documents()).Length} documentos registrados."),reference,devices,filters,until,bio,docs),MaxHeight=600},"Registrar revisión","Cancelar")!=ContentDialogResult.Primary)return;
        await session.ConfirmReview(sourceSet,new(reference.Text,devices.Text,filters.Text,session.Period.Start,
            DateOnly.FromDateTime(until.Date?.DateTime??throw new InvalidOperationException("Indica la fecha de cobertura.")),bio.IsChecked==true,docs.IsChecked==true),OperationLifetime.CurrentToken);
    }
    void MarksPage()
    {
        actions.Content=Line(Button("Importar archivo biométrico",async()=>
        {
            var p=await Pick(".xlsx");if(p==null)return;var preview=await session.PreviewBiometrics(p,OperationLifetime.CurrentToken);
            if(await Dialog("Revisar importación",Text($"{preview.Rows} filas leídas.\n{preview.NewRecords} registros nuevos; {preview.Duplicates} duplicados.\n{preview.IdentityPending} identidades pendientes; {preview.Invalid} filas inválidas.\nLos registros fuera del período se conservan con su origen."),"Importar","Cancelar")==ContentDialogResult.Primary)
                await session.ImportBiometrics(p,preview,OperationLifetime.CurrentToken);
        }));
        body.Children.Add(Table("Marcaciones importadas",[new("Servidor",280),new("Fecha",140),new("Hora",120),new("Origen",380)],session.Host.Marks.Select(m=>
            new PresentationRow(m.GetProperty("MarcacionID").GetString()!,[Person(m.GetProperty("ServidorID").GetString()!),m.GetProperty("Fecha").GetString()!,m.GetProperty("HoraOriginal").GetString()!,m.GetProperty("ArchivoFuente").GetString()!],m)).ToArray()).Root);
    }
    DocumentModule Module()=>page switch{"Comisiones"=>DocumentModule.COMMISSION,"Licencias"=>DocumentModule.LEAVE,"Vacaciones"=>DocumentModule.VACATION,"Justificaciones"=>DocumentModule.JUSTIFICATION,_=>DocumentModule.PERSONAL_PERMISSION};
    async Task DocumentsPage()
    {
        var module=Module();
        async Task Import(DocumentModule kind)
        {
            var p=await Pick(".xlsx");if(p==null)return;var preview=await session.PreviewDocuments(p,kind,OperationLifetime.CurrentToken);
            var message=$"{preview.Changes.Length} filas examinadas.\n"+string.Join("\n",preview.Changes.GroupBy(c=>c.Disposition).Select(g=>g.Count()+" · "+(g.Key switch{ImportDisposition.NEW_RECORD=>"documentos nuevos",ImportDisposition.EXISTING_UNCHANGED=>"documentos ya importados",ImportDisposition.INCOMPLETE_RECORD=>"registros con información pendiente",ImportDisposition.CONFLICT_REQUIRES_REVIEW=>"conflictos que requieren revisión",_=>"cambios documentales"})))+"\nImportar conserva los estados originales; no aprueba ni aplica documentos automáticamente.";
            if(await Dialog("Revisar documentos",Text(message),"Importar","Cancelar")==ContentDialogResult.Primary)
                await session.ImportDocuments(preview,OperationLifetime.CurrentToken);
        }
        var buttons=Line(Button("Importar "+page.ToLowerInvariant(),()=>Import(module)));
        if(page=="Permisos")buttons.Children.Add(Button("Importar permisos médicos",()=>Import(DocumentModule.MEDICAL_PERMISSION)));
        if(page=="Comisiones")buttons.Children.Add(Button("Importar registros CFE",()=>Import(DocumentModule.CFE)));
        if(page=="Justificaciones"){buttons.Children.Add(Button("Importar registros ECU 911",()=>Import(DocumentModule.ECU911)));buttons.Children.Add(Button("Importar cambios de Guardia",()=>Import(DocumentModule.GUARD_CHANGE)));}
        actions.Content=buttons;
        var docs=await session.Documents();
        var rows=docs.Where(d=>d.Data.Module==module||page=="Permisos"&&d.Data.Module==DocumentModule.MEDICAL_PERMISSION||page=="Comisiones"&&d.Data.Module==DocumentModule.CFE||page=="Justificaciones"&&d.Data.Module is DocumentModule.ECU911 or DocumentModule.GUARD_CHANGE).Select(d=>
            new PresentationRow(d.RecordId,[Person(d.Data.PersonId??""),d.Data.OriginalNumber??"Sin número",$"{d.Data.StartDate:dd/MM/yyyy} — {d.Data.EndDate:dd/MM/yyyy}",d.Data.ApprovalStatus,d.Data.Missing().Length==0?"Importado; aplicación según motor":"Información pendiente"],d)).ToArray();
        body.Children.Add(Table("Documentos",[new("Servidor",260),new("Documento",160),new("Intervalo",220),new("Aprobación original",180),new("Revisión",240)],rows).Root);
    }
    void CasesPage()
    {
        var management=ProjectEvents.Read<JsonElement>(session.Handle!.Work,"CBA_CENTER_MANAGEMENT_V1").GroupBy(e=>e.GetProperty("CaseId").GetString()!).ToDictionary(g=>g.Key,g=>g.Last(),StringComparer.Ordinal);
        var rows=session.LastRun?.Days.SelectMany(d=>d.Cases.Select(c=>new PresentationRow(c.Id,[Person(c.PersonId),c.Date.ToString("dd/MM/yyyy"),MonthlyPresentation.Case(c.Type),MonthlyPresentation.Minutes(c),c.Description],c))
            .Concat(d.Gaps.Select(g=>new PresentationRow(d.PersonId+d.Date,[Person(d.PersonId),d.Date.ToString("dd/MM/yyyy"),"Evidencia pendiente","No determinado",g],d)))).ToArray()??[];
        rows=rows.Select(row=>management.TryGetValue(row.Key,out var note)?row with{Cells=[..row.Cells,note.GetProperty("Responsible").GetString()!,note.GetProperty("Note").GetString()!]}:row with{Cells=[..row.Cells,"Sin asignar","Sin gestión registrada"]}).ToArray();
        var table=Table("Novedades y evidencia",[new("Servidor",260),new("Fecha",130),new("Novedad",230),new("Minutos",110),new("Detalle",430),new("Responsable",180),new("Gestión guardada",360)],rows);
        actions.Content=Line(Button("Procesar el mes",async()=>{await OperationalNative.Process(session,OperationLifetime.CurrentToken);}),Button("Guardar gestión del caso seleccionado",async()=>
        {
            var row=table.Selected??throw new InvalidOperationException("Selecciona una novedad.");
            var who=new TextBox{Header="Responsable",Text=session.Actor};var note=new TextBox{Header="Gestión administrativa",AcceptsReturn=true,MinHeight=100,TextWrapping=TextWrapping.Wrap};
            if(await Dialog("Gestión del Centro",Column(Text("La gestión no justifica la jornada ni modifica el resultado del motor."),who,note),"Guardar","Cancelar")==ContentDialogResult.Primary)
                await session.Event("CBA_CENTER_MANAGEMENT_V1",new{CaseId=row.Key,Responsible=who.Text,Note=note.Text,At=DateTimeOffset.UtcNow},OperationLifetime.CurrentToken);
        }));body.Children.Add(table.Root);
    }
    async Task DisciplinePage()
    {
        if(session.LastRun==null){body.Children.Add(Text("Procesa el período para consultar los controles admitidos. No se calculan descuentos ni se determinan responsabilidades."));return;}
        var facts=await OperationalFacts.Capture(session);var rows=new List<PresentationRow>();
        foreach(var population in new[]{"ADMIN","OPADMIN","DIRECTORES","GUARDIA"})
        {
            var ids=session.People.Where(p=>p.Population==population).Select(p=>p.Id).ToHashSet(StringComparer.Ordinal);
            var projection=D04Controls.Project(session.PeriodFor(population),facts.Where(f=>ids.Contains(f.PersonId)));
            rows.AddRange(projection.Counts.Select(c=>new PresentationRow(c.PersonId,[Person(c.PersonId),OperationalLabels.Population(population),c.LateDates.Length.ToString(),c.MissingDates.Length.ToString(),c.Minutes?.ToString()??"No determinado",projection.Alerts.Any(a=>a.PersonId==c.PersonId)?"Alerta para revisión humana":"Sin umbral alcanzado en hechos acreditados"],c)));
        }
        heading.Children.Add(Text("Control sobre hechos capturados y fuentes revisadas. No constituye sanción, descuento ni certificación laboral.",12));
        body.Children.Add(Table("Control disciplinario para revisión",[new("Servidor",250),new("Población",220),new("Fechas con atraso",140),new("Fechas sin marca",140),new("Minutos",110),new("Revisión",360)],rows.ToArray()).Root);
    }
    void MemosPage()
    {
        actions.Content=Line(Button("Generar borradores Word por servidor",async()=>{var token=OperationLifetime.CurrentToken;var files=await Task.Run(()=>OperationalArtifacts.Memoranda(session,token),token);if(files.Length==0)await Dialog("Sin requerimientos pendientes",Text("No hay novedades procesadas pendientes de justificación que permitan generar un borrador."),"Entendido");else await SaveCopies(files,files.ToDictionary(f=>f,f=>MonthlyPresentation.MemoFileName(f,session.People),StringComparer.Ordinal));}));
        body.Children.Add(Table("Memorandos guardados",[new("Documento",650),new("Estado",250)],session.Exports.Where(e=>e.Kind=="WORD_DRAFT").Select(e=>new PresentationRow(e.Id,[MonthlyPresentation.MemoFileName(e.Path,session.People),"Borrador guardado; sin emisión"],e)).ToArray()).Root);
    }
    void HolidaysPage()
    {
        actions.Content=Line(Button("Importar calendario de feriados",async()=>
        {
            var path=await Pick(".csv");if(path==null)return;var rows=OperationalCsv.Read(path,["Fecha","Descripción","Referencia"]);
            var hash=PhysicalMatrix.Hash(path);if(ProjectEvents.Read<JsonElement>(session.Handle!.Work,"CBA_HOLIDAYS_IMPORTED_PENDING_APPLICATION_V1").Any(e=>e.GetProperty("SHA256").GetString()==hash))return;
            var list=rows.Select(r=>new{Date=OperationalCsv.Date(r.Cells["Fecha"]),Description=r.Cells["Descripción"],Reference=r.Cells["Referencia"]}).ToArray();
            if(list.Any(r=>r.Date<session.Period.Start||r.Date>session.Period.End||r.Reference.Length==0))throw new InvalidDataException("Los feriados deben corresponder al período y tener referencia.");
            await session.Event("CBA_HOLIDAYS_IMPORTED_PENDING_APPLICATION_V1",new{SHA256=hash,Entries=list},OperationLifetime.CurrentToken);
        }));
        heading.Children.Add(Text("El calendario conserva su referencia. Su aplicación laboral requiere el contrato admitido; las jornadas afectadas quedan pendientes y no generan una ausencia por inferencia.",12));
        var calendar=ProjectEvents.Read<JsonElement>(session.Handle!.Work,"CBA_HOLIDAYS_IMPORTED_PENDING_APPLICATION_V1").SelectMany(e=>e.GetProperty("Entries").EnumerateArray()).ToArray();
        body.Children.Add(Table("Calendario importado",[new("Fecha",140),new("Descripción",360),new("Referencia",360),new("Aplicación",260)],calendar.Select((e,i)=>new PresentationRow(i.ToString(),[e.GetProperty("Date").GetString()!,e.GetProperty("Description").GetString()!,e.GetProperty("Reference").GetString()!,"Pendiente del contrato específico"],e)).ToArray()).Root);
    }
    async Task OpenMatrix()
    {
        var matrices=OperationalArtifacts.Matrices(session).Where(m=>m.RunId==session.LastRun?.Id).ToArray();
        if(matrices.Length==0)throw new InvalidOperationException("Genera primero las matrices del período.");
        var choices=new ComboBox{ItemsSource=matrices.Select(m=>MonthlyPresentation.MatrixName(m.Path)).ToArray(),SelectedIndex=0,MinWidth=460};
        if(await Dialog("Selecciona la matriz",choices,"Abrir","Cancelar")!=ContentDialogResult.Primary)return;
        var matrix=matrices[choices.SelectedIndex];
        var profile=OperationalArtifacts.Profile(session,matrix);
        var handle=session.Handle!;
        async Task<(string Summary,string Technical)?> Inspect(string selectedSheet,string coordinate)
        {
            if(!matrix.Bindings.TryGetValue(MatrixReview.Key(selectedSheet,coordinate),out var binding)||binding.PersonId==null||binding.Date==null)return null;
            var run=ProjectEvents.Read<MonthlyRun>(handle.Work,OperationalSession.RunEvent).Single(r=>r.Id==matrix.RunId);
            var day=run.Days.SingleOrDefault(d=>d.PersonId==binding.PersonId&&d.Date==binding.Date);
            if(day==null)return ("No hay una jornada acreditada para esa fecha. No se interpreta como cero ni como ausencia.",CanonicalMonthly.Json(binding));
            var lines=new List<string>{Person(day.PersonId),day.Date.ToString("dd/MM/yyyy"),"Resultado recibido: "+(day.Value??"Evidencia pendiente")};
            lines.AddRange(day.Gaps);lines.AddRange(day.Cases.Select(c=>MonthlyPresentation.Case(c.Type)+": "+MonthlyPresentation.Minutes(c)+". "+c.Description));
            if(day.Trace.TryGetProperty("MarksEvaluated",out var marks))lines.Add("Marcaciones: "+string.Join(" · ",marks.EnumerateArray().Select(m=>m.GetProperty("Fecha").GetString()+" "+m.GetProperty("HoraOriginal").GetString())));
            if(day.Trace.TryGetProperty("DocumentsExamined",out var docs))lines.AddRange(docs.Deserialize<DocumentRevision[]>()!.Select(d=>"Documento "+d.Data.OriginalNumber+" · "+OperationalImportTemplates.Label(d.Data.Module).Replace('_',' ')+" · "+d.Data.ApprovalStatus+" · "+d.Data.PhysicalDeliveryStatus));
            await Task.CompletedTask;return (string.Join("\n\n",lines),CanonicalMonthly.Json(new{Matrix=matrix.Id,matrix.SHA256,Day=day,Binding=binding}));
        }
        async Task<(string Summary,string Technical)?> InspectDocument(string selectedSheet,string coordinate)
        {
            if(PhysicalMatrix.Hash(matrix.Path)!=matrix.SHA256)throw new InvalidDataException("La matriz fuente cambió.");
            if(!matrix.Bindings.TryGetValue(MatrixReview.Key(selectedSheet,coordinate),out var binding)||binding.PersonId==null||binding.Date==null)return null;
            var run=ProjectEvents.Read<MonthlyRun>(handle.Work,OperationalSession.RunEvent).Single(r=>r.Id==matrix.RunId);
            await Task.CompletedTask;return MonthlyPresentation.Document(run,binding.PersonId,binding.Date.Value,binding.DocumentRecordId,Person(binding.PersonId));
        }
        var editor=new DetachedExcelWindow(session.Coordinator,session.Handle!,profile,matrix.Path,Path.Combine(session.Root,"exports"))
        {Title="CBA · "+MonthlyPresentation.MatrixName(matrix.Path),ProductActionCheck=()=>{OperationLifetime.Checkpoint();return Task.CompletedTask;},SpecificCausalDetail=Inspect,SpecificDocumentDetail=InspectDocument,SpecificHeaderCaption=MonthlyPresentation.PeriodCaption(matrix),SpecificPopulationExport=async()=>{var token=OperationLifetime.CurrentToken;return await Task.Run(()=>OperationalArtifacts.Export(session,matrix,token),token);}};
        editors.Add(editor);editor.Closed+=(_,_)=>editors.Remove(editor);editor.Activate();
        await Task.CompletedTask;
    }
    async Task<(string[] Files,IReadOnlyDictionary<string,string> Names)> ExportsWithNames(CancellationToken token)
    {
        var files=await OperationalArtifacts.ExportAll(session,token);
        var matrices=OperationalArtifacts.Matrices(session).Where(m=>m.RunId==session.LastRun!.Id).ToArray();
        if(files.Length!=matrices.Length)throw new InvalidDataException("No coincide la relación de matrices exportadas.");
        var month=session.PeriodFor("DIRECTORES").Start.ToString("MMMM yyyy",CultureInfo.GetCultureInfo("es-EC"));
        return(files,files.Select((file,i)=>(file,name:MonthlyPresentation.SafeFileName("Matriz - "+MonthlyPresentation.MatrixName(matrices[i].Path)+" - "+month+".xlsx"))).ToDictionary(p=>p.file,p=>p.name,StringComparer.Ordinal));
    }
    async Task SaveCopies(string[] files,IReadOnlyDictionary<string,string>? names=null)
    {
        var picker=new FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder=await picker.PickSingleFolderAsync().AsTask(OperationLifetime.CurrentToken);if(folder==null)return;
        var targetRoot=Path.GetFullPath(folder.Path)+Path.DirectorySeparatorChar;
        foreach(var protectedRoot in new[]{AppContext.BaseDirectory,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CBA","SistemaMarcaciones"),"C:\\Program Files\\Cuerpo de Bomberos de Ambato\\SistemaMarcacionesCBA"})
            if(targetRoot.StartsWith(Path.GetFullPath(protectedRoot).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Selecciona una carpeta de salida separada de los programas y de los datos existentes.");
        var saved=new List<string>();
        foreach(var source in files)
        {
            var hash=PhysicalMatrix.Hash(source);var target=Path.Combine(folder.Path,names?.GetValueOrDefault(source)??Path.GetFileName(source));
            if(File.Exists(target)&&PhysicalMatrix.Hash(target)!=hash)target=Path.Combine(folder.Path,Path.GetFileNameWithoutExtension(source)+"_"+hash[..8]+Path.GetExtension(source));
            if(!File.Exists(target))File.Copy(source,target,false);
            if(PhysicalMatrix.Hash(target)!=hash)throw new IOException("La copia de salida no coincide con el documento generado.");saved.Add(target);
        }
        await Dialog("Documentos guardados",Text(string.Join("\n",saved)),"Entendido");
    }
    async Task CloseSafely()
    {
        if(closingTask is {IsCompleted:false})return;
        closingTask=CloseCore();await closingTask;
    }
    async Task CloseCore()
    {
        if(editors.Any(e=>e.BlocksHostClose)){status.Text="Resuelve el diálogo o conflicto del editor antes de cerrar.";return;}
        closing=true;actions.IsEnabled=false;foreach(var b in menu.Values)b.IsEnabled=false;
        foreach(var e in editors.ToArray())e.FreezeForHostClose();
        OperationLifetime.BeginClosing();await Task.Yield();
        try
        {
            if(!(await OperationLifetime.DrainAsync(TimeSpan.FromSeconds(30))).Complete||!await work.Drain(TimeSpan.FromSeconds(30)))throw new InvalidOperationException("Hay una operación pendiente; no se ha forzado el cierre.");
            var windows=editors.ToArray();foreach(var e in windows)await e.PrepareForHostCloseAsync();
            if(!(await OperationLifetime.DrainAsync(TimeSpan.FromSeconds(30))).Complete)throw new InvalidOperationException("El guardado del editor sigue pendiente.");
            foreach(var e in windows)e.CommitHostClose();
            var done=Task.WhenAll(windows.Select(e=>e.ActualClose));if(await Task.WhenAny(done,Task.Delay(15000))!=done)throw new InvalidOperationException("No se confirmó el cierre del editor.");await done;
            if(!(await OperationLifetime.DrainAsync(TimeSpan.FromSeconds(30))).Complete)throw new InvalidOperationException("No se confirmó el fin de las operaciones.");
            OwnedLaunchBoundary.CloseSession();closePermitted=true;Close();
        }
        catch(Exception error)
        {
            PilotLog.Event("MONTHLY_CLOSE_NOT_CONFIRMED",error.ToString());status.Text=error.Message;
            if((await OperationLifetime.DrainAsync(TimeSpan.FromSeconds(1))).Complete&&OperationLifetime.ResumeAfterAbortedClose())
            {closing=false;foreach(var e in editors)e.ResumeAfterHostCloseAborted();foreach(var b in menu.Values)b.IsEnabled=true;actions.IsEnabled=true;}
        }
    }
}
