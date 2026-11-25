using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SistemaMarcaciones.Monthly.Controlled;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Monthly.Host.Wiring;
using SistemaMarcaciones.Monthly.Projects;
using SistemaMarcaciones.F13.Integrated.Native;

namespace SistemaMarcaciones.F13.Operational;

public sealed class OperationalSession
{
    public const string PersonnelKey="CBA_PERSONNEL:";
    public const string DutyKey="CBA_DUTY:";
    public const string SourceEvent="CBA_OPERATIONAL_IMPORT_V1";
    public const string ReviewEvent="CBA_SOURCE_REVIEW_V1";
    public const string RunEvent="CBA_MONTHLY_NATIVE_RUN_V1";
    public const string ExportEvent="CBA_OPERATIONAL_EXPORT_V1";
    public string Root {get;}
    public string TemplateRoot {get;set;}=Path.Combine(AppContext.BaseDirectory,"Templates");
    public ProjectApplicationService Projects {get;}
    public ProjectCoordinator Coordinator {get;}
    public ProjectHandle? Handle {get;private set;}
    public string Actor {get;}
    readonly LocalSourceReviewAuthority? reviewAuthority;
    readonly bool automatedQa;
    MonthlyWork? runWork;MonthlyRun? cachedRun;
    public bool IsSyntheticQa=>automatedQa;
    public string ExpectedReviewMode=>automatedQa?"AUTOMATED_QA_NOT_HUMAN_ACCEPTANCE":"EXPLICIT_LOCAL_OPERATOR_REVIEW";
    public string EvaluationProfile=>CanonicalMonthly.Hash(new{Mode=ExpectedReviewMode,Policy=reviewAuthority?.PolicySHA256});
    public MonthlyPeriod Period=>Handle?.Service.ActiveProject?.Period??throw new InvalidOperationException("Abre un trabajo mensual.");
    public OperationalSession(string root,string actor,LocalSourceReviewAuthority? authority=null,bool automatedQa=false)
    {
        Root=SistemaMarcaciones.Workspace.StorageV2.PathSafety.Sandbox(root);
        Actor=string.IsNullOrWhiteSpace(actor)?throw new ArgumentException("Identifica al responsable."):actor;
        reviewAuthority=authority;
        this.automatedQa=automatedQa;
        Projects=new(root);
        Coordinator=new(root);
    }
    public Task Initialize(CancellationToken ct=default)=>Projects.Initialize(ct);
    public Task<ProjectSummary[]> Summaries(CancellationToken ct=default)=>Projects.Hub.Summaries(ct);
    public async Task Create(int year,int month,CancellationToken ct=default)
    {
        var selected=new DateOnly(year,month,1);
        var period=new MonthlyPeriod(selected.AddMonths(-1).AddDays(20),new(year,month,DateTime.DaysInMonth(year,month)),"CUATRO_POBLACIONES");
        var descriptor=await Projects.Hub.Create(period,"Matrices de novedades "+selected.ToString("MMMM yyyy",System.Globalization.CultureInfo.GetCultureInfo("es-EC")),Actor,DateTimeOffset.UtcNow,ct);
        await Open(descriptor.Id,ct);
        if(!ProjectEvents.Read<MonthSelection>(Handle!.Work,"CBA_MONTH_SELECTION_V1").Any())await Event("CBA_MONTH_SELECTION_V1",new MonthSelection(year,month),ct);
    }
    public sealed record MonthSelection(int Year,int Month);
    public MonthlyPeriod PeriodFor(string population)
    {
        var month=ProjectEvents.Read<MonthSelection>(RequireHandle().Work,"CBA_MONTH_SELECTION_V1").LastOrDefault()
            ??throw new InvalidOperationException("El trabajo no tiene un mes de cierre explícito.");
        var first=new DateOnly(month.Year,month.Month,1);
        return population=="DIRECTORES"?new(first,first.AddMonths(1).AddDays(-1),population):new(first.AddMonths(-1).AddDays(20),first.AddDays(19),population);
    }
    public async Task Open(string id,CancellationToken ct=default)
    {
        Handle=await Coordinator.Open(id,ct);
        await Handle.Service.Reload(ct);
    }
    public PersonnelRow[] People=>Records<PersonnelRow>(PersonnelKey);
    public DutyRow[] Duties=>Records<DutyRow>(DutyKey);
    public ImportedSource[] Sources=>Handle==null?[]:ProjectEvents.Read<ImportedSource>(Handle.Work,SourceEvent).ToArray();
    public MonthlyRun? LastRun
    {get{var work=Handle?.Work;if(work==null)return null;if(!ReferenceEquals(work,runWork)){cachedRun=ProjectEvents.Read<MonthlyRun>(work,RunEvent).LastOrDefault();runWork=work;}return cachedRun;}}
    public SourceReview? Review=>Handle==null?null:ProjectEvents.Read<SourceReview>(Handle.Work,ReviewEvent).LastOrDefault();
    public bool MatchesProfile(MonthlyRun run)=>run.EvaluationProfile!=null?run.EvaluationProfile==EvaluationProfile:
        Review is {} review&&review.ReviewMode==ExpectedReviewMode&&review.PolicySHA256==reviewAuthority?.PolicySHA256&&run.ReviewSHA256==CanonicalMonthly.Hash(review);
    public LocalExport[] Exports=>Handle==null?[]:ProjectEvents.Read<LocalExport>(Handle.Work,ExportEvent).ToArray();
    T[] Records<T>(string prefix)=>Handle?.Work.Versions.LastOrDefault()?.Records
        .Where(p=>p.Key.StartsWith(prefix,StringComparison.Ordinal))
        .Select(p=>JsonSerializer.Deserialize<T>(p.Value)??throw new InvalidDataException("Registro guardado vacío.")).ToArray()??[];
    public MonthlyHostCommands Host=>new(Coordinator,RequireHandle(),People.SelectMany(p=>new[]{(Code:p.Identity,p.Id),(Code:p.BiometricIdentity??p.Identity,p.Id)})
        .GroupBy(p=>p.Code,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.Select(p=>p.Id).Distinct().Single(),StringComparer.Ordinal));
    ProjectHandle RequireHandle()=>Handle??throw new InvalidOperationException("Crea o abre un trabajo mensual.");
    public async Task<DocumentRevision[]> Documents(CancellationToken ct=default)=>
        SharedDocumentCatalog.ForPeriod(await Coordinator.Documents(ct),Period);
    public async Task<string> SourceSet(CancellationToken ct=default)
    {
        var documents=await Documents(ct);
        return CanonicalMonthly.Hash(new{
            Period,People,Duties,Sources,Holidays=ProjectEvents.Read<System.Text.Json.JsonElement>(RequireHandle().Work,"CBA_HOLIDAYS_IMPORTED_PENDING_APPLICATION_V1").ToArray(),
            Biometrics=Host.Sources.Select(s=>s.Source.SHA256).Order(StringComparer.Ordinal).ToArray(),
            Documents=documents.Where(d=>People.Any(p=>p.Id==d.Data.PersonId))
                .Select(d=>new{d.RecordId,d.Version,d.FunctionalHash}).OrderBy(d=>d.RecordId,StringComparer.Ordinal).ToArray()
        });
    }
    public async Task<bool> ReviewCurrent(CancellationToken ct=default)
    {
        var current=await SourceSet(ct);
        var review=ProjectEvents.Read<SourceReview>(RequireHandle().Work,ReviewEvent).LastOrDefault();
        if(reviewAuthority==null||review==null||review.SourceSetSHA256!=current
            ||review.PolicySHA256!=reviewAuthority.PolicySHA256||review.ReviewMode!=ExpectedReviewMode)return false;
        return review.ReviewerWindowsSid==reviewAuthority.RequireReviewer()
            &&review.Declaration.From<=Period.Start&&review.Declaration.Until>=Period.End
            &&review.Declaration.BiometricComplete&&review.Declaration.DocumentarySearchComplete;
    }
    public async Task ConfirmReview(string sourceSet,CoverageReviewDeclaration declaration,CancellationToken ct=default)
    {
        if(People.Length==0||Duties.Length==0)throw new InvalidOperationException("Carga el personal y las jornadas documentadas antes de verificar las fuentes.");
        if(await SourceSet(ct)!=sourceSet)throw new InvalidOperationException("Las fuentes cambiaron. Revisa la información actual antes de confirmar.");
        var authority=reviewAuthority??throw new InvalidOperationException("No hay un responsable admitido para verificar las fuentes.");
        if(Host.Sources.Length==0)throw new InvalidOperationException("Importa la fuente biométrica antes de verificar su cobertura.");
        var previews=ProjectEvents.Read<System.Text.Json.JsonElement>(RequireHandle().Work,"CBA_BIOMETRIC_EXTRACTION_REVIEW_V1").ToArray();
        if(previews.Any(p=>p.GetProperty("Invalid").GetInt32()>0||p.GetProperty("IdentityPending").GetInt32()>0))
            throw new InvalidOperationException("Hay filas biométricas inválidas o sin identidad exacta. No puede acreditarse una cobertura completa.");
        if((await Documents(ct)).Any(d=>d.Data.PersonId==null))throw new InvalidOperationException("Hay documentos sin beneficiario exacto. Revisa su vinculación antes de declarar completa la búsqueda.");
        if(Duties.Any(d=>d.Kind=="GUARDIA"&&d.End>DateTime.SpecifyKind(declaration.Until.ToDateTime(TimeOnly.MaxValue),DateTimeKind.Unspecified)))
            throw new InvalidOperationException("El alcance debe incluir el cierre D+1 de las guardias del período.");
        if(automatedQa&&People.Any(p=>!p.Name.StartsWith("QA_",StringComparison.Ordinal)))throw new InvalidOperationException("La revisión automática solo admite identidades sintéticas explícitas.");
        var review=authority.Review(sourceSet,Period,declaration,Actor,automatedQa);
        OperationalCoverage.Store(this,review);
        await Event(ReviewEvent,review,ct);
    }
    public async Task<int> ImportPeople(string path,CancellationToken ct=default)
    {
        var h=RequireHandle();var hash=PhysicalMatrix.Hash(path);
        if(Sources.Any(s=>s.Kind=="PERSONNEL"&&s.SHA256==hash))return 0;
        var rows=OperationalCsv.Read(path,["Cédula","Nombres y apellidos","Cargo","Población","Desde","Hasta"]);
        var people=rows.Select(r=>
        {
            var c=r.Cells;var identity=c["Cédula"];
            if(identity.Length==0||identity.Any(char.IsWhiteSpace)||c["Nombres y apellidos"].Length==0)
                throw new InvalidDataException("Falta la cédula exacta o el nombre en la fila "+r.Number+".");
            var population=OperationalLabels.PopulationCode(c["Población"]);
            var from=OperationalCsv.Date(c["Desde"]);var until=OperationalCsv.Date(c["Hasta"]);
            if(until<from)throw new InvalidDataException("La vigencia del personal está invertida.");
            return new PersonnelRow("SRV-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24],
                identity,c["Nombres y apellidos"],c["Cargo"],population,from,until,hash,r.Number,c.TryGetValue("Código biométrico",out var code)&&code.Length>0?code:null);
        }).ToArray();
        if(people.Select(p=>p.Identity).Distinct(StringComparer.Ordinal).Count()!=people.Length)
            throw new InvalidDataException("La carga contiene dos registros para la misma cédula. Resuelve su vigencia antes de continuar.");
        if(people.SelectMany(p=>new[]{(Code:p.Identity,p.Id),(Code:p.BiometricIdentity??p.Identity,p.Id)}).GroupBy(p=>p.Code,StringComparer.Ordinal).Any(g=>g.Select(p=>p.Id).Distinct().Count()>1))
            throw new InvalidDataException("Un código biométrico corresponde a más de un servidor. Corrige la vinculación exacta.");
        foreach(var p in people)
        {
            var old=People.SingleOrDefault(o=>o.Id==p.Id);
            if(old!=null&&old!=p)throw new InvalidOperationException("El servidor ya tiene otra versión. Se requiere revisar la corrección antes de reemplazarla.");
        }
        await CommitRecords(people.ToDictionary(p=>PersonnelKey+p.Id,p=>CanonicalMonthly.Json(p)),new("PERSONNEL",Path.GetFileName(path),hash,people.Length),ct);
        return people.Length;
    }
    public async Task<int> ImportDuties(string path,CancellationToken ct=default)
    {
        var hash=PhysicalMatrix.Hash(path);
        if(Sources.Any(s=>s.Kind=="DUTIES"&&s.SHA256==hash))return 0;
        var rows=OperationalCsv.Read(path,["Cédula","Fecha","Inicio","Fin","Tipo","Pelotón","Referencia","Estado"]);
        var duties=rows.Select(r=>
        {
            var c=r.Cells;
            var person=People.SingleOrDefault(p=>p.Identity==c["Cédula"])
                ??throw new InvalidDataException("No hay identidad exacta de personal para la fila "+r.Number+".");
            var date=OperationalCsv.Date(c["Fecha"]);
            var cut=PeriodFor(person.Population);
            var start=OperationalCsv.Stamp(c["Inicio"]);var end=OperationalCsv.Stamp(c["Fin"]);
            if(end<=start||date<cut.Start||date>cut.End||date<person.From||date>person.Until||DateOnly.FromDateTime(start)!=date)
                throw new InvalidDataException("La jornada de la fila "+r.Number+" no corresponde al período o a la vigencia del servidor.");
            if(c["Referencia"].Length==0)throw new InvalidDataException("La jornada requiere una referencia documental.");
            return new DutyRow(person.Id,date,start,end,c["Tipo"].ToUpperInvariant()=="DESCANSO"?"DESCANSO":OperationalLabels.PopulationCode(c["Tipo"]),c["Pelotón"],c["Referencia"],c["Estado"].ToUpperInvariant(),hash,r.Number);
        }).ToArray();
        if(duties.Select(d=>(d.PersonId,d.Date)).Distinct().Count()!=duties.Length)
            throw new InvalidDataException("Hay jornadas contradictorias para el mismo servidor y fecha.");
        foreach(var duty in duties)
        {
            var prior=Duties.SingleOrDefault(d=>d.PersonId==duty.PersonId&&d.Date==duty.Date);
            if(prior!=null&&prior!=duty)throw new InvalidOperationException("Existe otra jornada para esa fecha. Revisa la corrección antes de cambiarla.");
        }
        await CommitRecords(duties.ToDictionary(d=>DutyKey+d.PersonId+":"+d.Date.ToString("yyyy-MM-dd"),d=>CanonicalMonthly.Json(d)),new("DUTIES",Path.GetFileName(path),hash,duties.Length),ct);
        return duties.Length;
    }
    public async Task<BiometricImportPreview> PreviewBiometrics(string path,CancellationToken ct=default)
    {
        if(People.Length==0)throw new InvalidOperationException("Carga primero el personal con sus cédulas exactas.");
        return await Host.PreviewBiometrics(path,ct);
    }
    public async Task ImportBiometrics(string path,BiometricImportPreview preview,CancellationToken ct=default)
    {
        await Host.ImportBiometrics(path,preview,Actor,DateTimeOffset.UtcNow,ct);
        await Event("CBA_BIOMETRIC_EXTRACTION_REVIEW_V1",new{preview.SHA256,preview.Invalid,preview.IdentityPending},ct);
    }
    public async Task<ImportPreview> PreviewDocuments(string path,DocumentModule module,CancellationToken ct=default)
    {
        if(People.Length==0)throw new InvalidOperationException("Carga primero el personal.");
        var extraction=await Task.Run(()=>RealStructuredDocumentImporter.Read(path,module,
            People.ToDictionary(p=>p.Identity,p=>p.Id,StringComparer.Ordinal),RealDocumentIdentities.Empty,DateTimeOffset.UtcNow),ct);
        return SharedDocumentCatalog.Preview(await Coordinator.Documents(ct),module,extraction.Records);
    }
    public async Task<ImportCommit> ImportDocuments(ImportPreview preview,CancellationToken ct=default)
    {
        ImportCommit? result=null;
        await OperationLifetime.RunAsync("Confirmar documentos","CSHARP_MONTHLY_LOCAL",async()=>
        {ct.ThrowIfCancellationRequested();using var commit=OperationLifetime.EnterCommit();result=await Coordinator.CommitDocuments(RequireHandle(),preview,Actor,DateTimeOffset.UtcNow,CancellationToken.None);});
        return result??throw new InvalidOperationException("No se confirmó la importación documental.");
    }
    Task CommitRecords(IReadOnlyDictionary<string,string> changed,ImportedSource source,CancellationToken ct)=>
        OperationLifetime.RunAsync("Confirmar fuentes","CSHARP_MONTHLY_LOCAL",()=>CommitRecordsCore(changed,source,ct));
    async Task CommitRecordsCore(IReadOnlyDictionary<string,string> changed,ImportedSource source,CancellationToken ct)
    {
        var h=RequireHandle();await h.Serial.WaitAsync(ct);
        try
        {
            await h.Service.Reload(ct);ProjectCoordinator.RequireWritable(h);
            var before=h.Work;var records=new Dictionary<string,string>(before.Versions.LastOrDefault()?.Records??new Dictionary<string,string>(),StringComparer.Ordinal);
            foreach(var record in changed)records[record.Key]=record.Value;
            var pins=(before.Versions.LastOrDefault()?.Sources??[]).Append(new SourcePin(source.Kind+":"+source.SHA256,source.SHA256,"CBA_OPERATIONAL_IMPORT_V1")).Distinct().ToArray();
            var next=MonthlyWorkflow.NewVersion(before,Period,pins,"CONTINUIDAD-20261001",records,Actor,DateTimeOffset.UtcNow);
            next=ProjectEvents.Append(next,SourceEvent,source,Actor,DateTimeOffset.UtcNow);
            ct.ThrowIfCancellationRequested();
            using var commit=OperationLifetime.EnterCommit();
            await h.Repository.Save(next,before.Revision,CancellationToken.None);
            await h.Service.Reload(CancellationToken.None);
        }
        finally{h.Serial.Release();}
    }
    public Task Event<T>(string kind,T value,CancellationToken ct=default)=>
        OperationLifetime.RunAsync("Guardar movimiento","CSHARP_MONTHLY_LOCAL",()=>EventCore(kind,value,ct));
    async Task EventCore<T>(string kind,T value,CancellationToken ct)
    {
        var h=RequireHandle();await h.Serial.WaitAsync(ct);
        try
        {
            await h.Service.Reload(ct);var before=h.Work;
            var next=ProjectEvents.Append(before,kind,value,Actor,DateTimeOffset.UtcNow);
            ct.ThrowIfCancellationRequested();
            using var commit=OperationLifetime.EnterCommit();
            await h.Repository.Save(next,before.Revision,CancellationToken.None);
            await h.Service.Reload(CancellationToken.None);
        }
        finally{h.Serial.Release();}
    }
}
