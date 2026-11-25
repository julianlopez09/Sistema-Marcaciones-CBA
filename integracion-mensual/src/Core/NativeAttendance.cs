using System.Text;
using System.Text.Json;
using SistemaMarcaciones.Centro.Presentation;
using SistemaMarcaciones.Monthly.Controlled;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Monthly.Export.Verified;
using SistemaMarcaciones.Monthly.ForwardAttendance;
using SistemaMarcaciones.Monthly.Host.NativeIntegration;
using SistemaMarcaciones.Monthly.Host.Wiring;
using SistemaMarcaciones.Monthly.Management;
using SistemaMarcaciones.Monthly.Native.Documentary;
using SistemaMarcaciones.Monthly.Native.Outcomes;
using SistemaMarcaciones.Monthly.Projects;
using SistemaMarcaciones.Workspace.StorageV2;

namespace SistemaMarcaciones.F13.Integrated.Native;

public sealed record NativeOrigin(string Kind,string ExecutionId,string PayloadSHA256,int CommitRevision,string ImplementationSHA256);
public sealed record NativeAttendanceBundle(string ProjectId,MonthlyPeriod Period,NativeOrigin[] Origins,
    VerifiedFact[] Facts,B07Completion[] Completions,FactWitness[] CompletionMetadata);
public sealed record NativeMatrixRegistration(string Contract,string Id,string ProjectId,string SourceSetSHA256,
    int SourceRevision,string MatrixId,AttendanceWorkbookReceipt Workbook,string NativeSidecar,string NativeSidecarSHA256);
public sealed record NativeCellDetail(string PersonId,DateOnly Date,string State,VerifiedFact[] Results,
    B07Completion[] CompletionEvidence,string Authority,bool LabourRecomputed=false,decimal? EconomicAmount=null);
public sealed record NativeExportLink(string Contract,string MatrixId,int SavedRevision,string SourceSetSHA256,
    ReviewedExport Export,string EvidencePath,string EvidenceSHA256);

/// <summary>Additive receipt boundary. Never writes a synthetic historical Host execution event.</summary>
public sealed class NativeAttendanceCoordinator(B09Session session)
{
    public const string Contract="F13_NATIVE_ATTENDANCE_REGISTRATION_V1";
    public const string ExportKind="F13_NATIVE_ATTENDANCE_EXPORT_LINK_V1";
    public event Action<CanonicalChange>? Committed;
    public NativeMatrixRegistration[] Registrations=>ProjectEvents.Read<NativeMatrixRegistration>(session.Handle.Work,Contract).ToArray();
    public static NativeAttendanceBundle Collect(B09Session session)
    {
        var origins=new List<NativeOrigin>();var all=new List<VerifiedFact>();var completions=new List<B07Completion>();var metadata=new List<FactWitness>();
        foreach(var n in session.Host.NativeExecutions)
        {
            if(n.Identity.ImplementationSHA256!=NativeEngineIdentity.Current.ImplementationSHA256
                ||n.Receipt.ProjectId!=session.Handle.Id||!n.Receipt.TechnicalFixture)
                throw new InvalidDataException("NATIVE_ORIGIN_IDENTITY_MISMATCH");
            foreach(var f in n.Receipt.Facts)
                if(f.Trace.GetProperty("Engine").GetString()!=NativeEngineIdentity.Current.Engine
                    ||f.Trace.GetProperty("ImplementationSHA256").GetString()!=n.Identity.ImplementationSHA256
                    ||!n.UnitInputHashes.Contains(f.Trace.GetProperty("InputSHA256").GetString(),StringComparer.Ordinal))
                    throw new InvalidDataException("NATIVE_FACT_NOT_BOUND_TO_EXECUTED_INPUT");
            all.AddRange(n.Receipt.Facts);
            origins.Add(new(MonthlyHostCommands.NativeExecutionKind,n.Receipt.ExecutionId,CanonicalMonthly.Hash(n),n.Receipt.InputRevision+1,n.Identity.ImplementationSHA256));
        }
        foreach(var d in session.ReadDocumentary())
        {
            var e=ProjectEvents.Read<B09ResultCommit>(session.Handle.Work,B09Session.DocumentaryKind).Single(e=>e.ExecutionId==d.ExecutionId);
            all.AddRange(d.Facts);origins.Add(new(B09Session.DocumentaryKind,d.ExecutionId,CanonicalMonthly.Hash(d),e.Revision,d.ImplementationSHA256));
        }
        var outcomeCoordinator=new B07Coordinator(session.Handle,new(session.Configuration.ProjectRoot,true,true,B07Contract.Authority,B07Identity.Current.ImplementationSHA256),new());
        foreach(var d in session.ReadOutcomes())
        {
            if(outcomeCoordinator.IsStale(d))throw new InvalidDataException("STALE_NATIVE_COMPLETION_CANNOT_RENDER");
            completions.Add(d.Completion);all.AddRange(d.Completion.Facts);
            var input=JsonSerializer.Deserialize<B07Request>(session.Handle.Work.Current.Records[B07Contract.InputPrefix+d.Completion.UnitId],B07Contract.Wire)!;
            input.Native.MetadataWitness.Validate();metadata.Add(input.Native.MetadataWitness);
            // Physical observation revision/hash are deliberately NOT the stable source identity.
            origins.Add(new(B07Contract.Event,d.ReceiptId,CanonicalMonthly.Hash(d.Completion),d.Commit.ConfirmedRevision,d.Completion.Engine.ImplementationSHA256));
        }
        var facts=all.GroupBy(f=>f.CaseId,StringComparer.Ordinal).Select(g=>
        {
            if(g.Select(CanonicalMonthly.Hash).Distinct(StringComparer.Ordinal).Count()!=1)throw new InvalidDataException("NATIVE_RESULT_CONFLICT");
            var f=g.First();f.Validate();
            if(!session.Witness.People.Contains(f.PersonId,StringComparer.Ordinal)||f.Date<session.Configuration.Period.Start||f.Date>session.Configuration.Period.End
                ||f.Classification is not ("ADMINISTRATIVO" or "OPERATIVO_EN_LABORES_ADMINISTRATIVAS")
                ||f.Trace.GetProperty("Engine").GetString()=="ACCEPTED_PYTHON_F4_BINDING_V1")
                throw new InvalidDataException("NATIVE_FACT_SCOPE_NOT_ADMITTED");
            return f;
        }).OrderBy(f=>f.PersonId,StringComparer.Ordinal).ThenBy(f=>f.Date).ThenBy(f=>f.CaseId,StringComparer.Ordinal).ToArray();
        if(origins.Select(o=>o.ExecutionId).Distinct(StringComparer.Ordinal).Count()!=origins.Count)throw new InvalidDataException("DUPLICATE_NATIVE_EXECUTION");
        return new(session.Handle.Id,session.Configuration.Period,origins.OrderBy(o=>o.ExecutionId,StringComparer.Ordinal).ToArray(),facts,
            completions.OrderBy(c=>c.PersonId,StringComparer.Ordinal).ThenBy(c=>c.Date).ToArray(),metadata.Distinct().OrderBy(m=>m.Location,StringComparer.Ordinal).ToArray());
    }
    public static AttendanceProjection Project(NativeAttendanceBundle bundle)
    {
        if(bundle.Origins.Length==0)throw new InvalidDataException("NO_DURABLE_NATIVE_EXECUTION_AVAILABLE");
        // Transport adapter only; this receipt is never persisted as a historical execution.
        var execution=new HostExecutionReceipt(CanonicalMonthly.Hash(bundle.Origins),bundle.ProjectId,0,CanonicalMonthly.Hash(bundle),bundle.Facts,[],[],true,0);
        var p=ForwardAttendanceDateGridAdapter.Project(bundle.ProjectId,bundle.Period,execution);
        var people=p.People.ToList();var cells=p.Cells.ToList();
        foreach(var completion in bundle.Completions)
        {
            if(completion.ProjectId!=bundle.ProjectId||completion.Period!=bundle.Period)throw new InvalidDataException("COMPLETION_PERIOD_MISMATCH");
            if(people.All(person=>person.PersonId!=completion.PersonId))
            {
                var metadata=bundle.CompletionMetadata.Single(m=>m.Location=="master_records/servidores/record_id="+completion.PersonId);
                metadata.Validate();
                people.Add(new(completion.PersonId,completion.Classification=="ADMINISTRATIVO"?AttendancePopulation.ADMIN:AttendancePopulation.OPADMIN,
                    completion.Classification,null,null,metadata));
                foreach(var date in p.Dates)cells.Add(new(completion.PersonId,date,people[^1].Population,[],"NO_RESULT_AVAILABLE",null,null));
            }
            if(completion.IntendedOutcome==B07State.COMPLETED_NO_NOVELTIES)
            {
                var index=cells.FindIndex(c=>c.PersonId==completion.PersonId&&c.Date==completion.Date);
                if(index<0||cells[index].Results.Length!=0)throw new InvalidDataException("NO_NOVELTY_CONFLICT_WITH_FACTS");
                cells[index]=cells[index] with{PresentationState="COMPLETED_NO_NOVELTIES",PresentationValue="SIN NOVEDADES EN ALCANCE PROCESADO",PresentationCategory=null};
            }
        }
        return p with{People=people.OrderBy(x=>x.PersonId,StringComparer.Ordinal).ToArray(),Cells=cells.OrderBy(c=>c.PersonId,StringComparer.Ordinal).ThenBy(c=>c.Date).ToArray()};
    }
    public async Task<NativeMatrixRegistration> Generate(string stencil,string sandbox,int expectedRevision,string actor,DateTimeOffset at,CancellationToken ct=default)
    {
        ProjectCoordinator.RequireWritable(session.Handle);ct.ThrowIfCancellationRequested();
        var h=session.Handle;var bundle=Collect(session);var sourceHash=CanonicalMonthly.Hash(bundle);var projection=Project(bundle);
        var id=CanonicalMonthly.Hash(new{Contract,sourceHash,Stencil=AttendanceWorkbook.AcceptedAnonymousStencilSHA});
        var prior=Registrations.SingleOrDefault(r=>r.Id==id);
        if(prior!=null){Verify(prior);return prior;}
        if(h.Work.Revision!=expectedRevision)throw new InvalidOperationException("CAS_NATIVE_MATRIX_SOURCE_STALE");
        var receipt=await Task.Run(()=>AttendanceWorkbook.Render(stencil,AttendanceWorkbook.AcceptedAnonymousStencilSHA,sandbox,projection),ct);
        ct.ThrowIfCancellationRequested();
        var nativeSidecar=Path.Combine(Path.GetDirectoryName(receipt.SidecarPath)!,"native_execution_evidence.json");
        WriteExact(nativeSidecar,new{Contract,SourceSetSHA256=sourceHash,Bundle=bundle,ProjectionSHA256=CanonicalMonthly.Hash(projection),
            CompletionEvidenceAuthority="ENGINE_EXECUTION_TRACE_COMPLETE_B07_V1_NOT_A_VERIFIED_FACT",NoEconomicCalculation=true,NoHistoricalReceiptPromotion=true});
        var profile=WorkbookProfiles.Read(receipt.Asset.FileName,receipt.Asset.SHA256,receipt.Asset.Profile,bundle.Period,true,receipt.Bindings);
        await h.Serial.WaitAsync(ct);try
        {
            await h.Service.Reload(ct);ProjectCoordinator.RequireWritable(h);ct.ThrowIfCancellationRequested();
            if(h.Work.Revision!=expectedRevision||CanonicalMonthly.Hash(Collect(session))!=sourceHash)throw new InvalidOperationException("CAS_NATIVE_MATRIX_SOURCE_CHANGED");
            var registration=new NativeMatrixRegistration(Contract,id,h.Id,sourceHash,expectedRevision,profile.Draft.Id,receipt,nativeSidecar,PhysicalMatrix.Hash(nativeSidecar));
            var next=MatrixReview.Add(h.Work,profile.Draft,actor,at);
            next=ProjectEvents.Append(next,Contract,registration,actor,at);
            await h.Repository.Save(next,expectedRevision,ct);await h.Service.Reload(CancellationToken.None);
            var confirmed=Registrations.Single(r=>r.Id==id);Verify(confirmed);
            foreach(Action<CanonicalChange> handler in Committed?.GetInvocationList()??[])
                try{handler(new(h.Id,h.Work.Revision,"NATIVE_ATTENDANCE_DURABLY_COMMITTED",[registration.MatrixId]));}catch{/* durable commit cannot be undone by a view */}
            return confirmed;
        }finally{h.Serial.Release();}
    }
    public static void Verify(NativeMatrixRegistration r)
    {
        if(r.Contract!=Contract||PhysicalMatrix.Hash(r.Workbook.Asset.FileName)!=r.Workbook.Asset.SHA256
            ||PhysicalMatrix.Hash(r.Workbook.SidecarPath)!=r.Workbook.SidecarSHA256||PhysicalMatrix.Hash(r.NativeSidecar)!=r.NativeSidecarSHA256)
            throw new InvalidDataException("NATIVE_MATRIX_OR_EVIDENCE_CHANGED");
        using var side=JsonDocument.Parse(File.ReadAllText(r.NativeSidecar));
        var b=side.RootElement.GetProperty("Bundle").Deserialize<NativeAttendanceBundle>(B07Contract.Wire)!;
        if(b.ProjectId!=r.ProjectId||CanonicalMonthly.Hash(b)!=r.SourceSetSHA256||side.RootElement.GetProperty("ProjectionSHA256").GetString()!=r.Workbook.ProjectionSHA256
            ||CanonicalMonthly.Hash(Project(b))!=r.Workbook.ProjectionSHA256)
            throw new InvalidDataException("NATIVE_WORKBOOK_SOURCE_LINK_MISMATCH");
        foreach(var f in b.Facts)f.Validate();
        foreach(var c in b.Completions)B07CompletionValidator.Validate(c);
    }
    public WorkbookProfile ReadProfile(NativeMatrixRegistration r)
    {Verify(r);if(r.ProjectId!=session.Handle.Id||r.Workbook.Asset.SourcePeriod!=session.Configuration.Period)throw new InvalidDataException("NATIVE_PROFILE_SCOPE");return WorkbookProfiles.Read(r.Workbook.Asset.FileName,r.Workbook.Asset.SHA256,r.Workbook.Asset.Profile,r.Workbook.Asset.SourcePeriod,true,r.Workbook.Bindings);}
    public static NativeCellDetail? Detail(MonthlyWork work,string matrix,string sheet,string coordinate)
    {
        var r=ProjectEvents.Read<NativeMatrixRegistration>(work,Contract).SingleOrDefault(r=>r.MatrixId==matrix);if(r==null)return null;
        Verify(r);var locator=r.Workbook.Locators.SingleOrDefault(l=>l.Sheet==sheet&&l.Coordinate==coordinate);if(locator==null)return null;
        using var side=JsonDocument.Parse(File.ReadAllText(r.NativeSidecar));var bundle=side.RootElement.GetProperty("Bundle").Deserialize<NativeAttendanceBundle>(B07Contract.Wire)!;
        var cell=Project(bundle).Cells.Single(c=>c.PersonId==locator.PersonId&&c.Date==locator.Date);
        return new(cell.PersonId,cell.Date,cell.PresentationState,cell.Results,bundle.Completions.Where(c=>c.PersonId==cell.PersonId&&c.Date==cell.Date).ToArray(),
            cell.PresentationState=="COMPLETED_NO_NOVELTIES"?B07Contract.Trace:cell.Results.Length>0?"ENGINE_TRACE_COMPLETE":"NO_RESULT_AVAILABLE");
    }
    public CentroDataset CenterDataset()=>session.ReadModel().Center;
    public CentroViewModel Center(string actor)=>new(session.Handle.Repository,CenterDataset(),actor,()=>DateTimeOffset.UtcNow);
    public async Task<NativeExportLink> Export(NativeMatrixRegistration r,string destination,string actor,DateTimeOffset at,CancellationToken ct=default)
    {
        Verify(r);var savedRevision=session.Handle.Work.Revision;
        var receipt=await session.Projects.Export(session.Handle,r.MatrixId,r.Workbook.Asset.FileName,destination,actor,at,ct);
        var detail=new{Contract=ExportKind,r.MatrixId,SavedRevision=savedRevision,r.SourceSetSHA256,NativeOrigins=Collect(session).Origins,
            Pending=CrossMatrixReview.For(session.Handle.Work).Matrix(r.MatrixId),Status="F13_RENDERED_PREVIEW_XLSX_NOT_CERTIFIED",
            EconomicRecalculated=false,LabourReevaluated=false,OriginalEconomicValuesAltered=false,ExportSHA256=receipt.SHA256};
        var file=Path.Combine(destination,receipt.FileName+".native-evidence.json");WriteExact(file,detail);
        var link=new NativeExportLink(ExportKind,r.MatrixId,savedRevision,r.SourceSetSHA256,receipt,file,PhysicalMatrix.Hash(file));
        await session.Handle.Serial.WaitAsync(ct);try
        {
            var h=session.Handle;await h.Service.Reload(ct);var before=h.Work;
            if(!ProjectEvents.Read<NativeExportLink>(before,ExportKind).Any(x=>x.Export.SHA256==receipt.SHA256))
            {await h.Repository.Save(ProjectEvents.Append(before,ExportKind,link,actor,at),before.Revision,ct);await h.Service.Reload(ct);}
        }finally{session.Handle.Serial.Release();}
        return link;
    }
    public static void WriteExact<T>(string path,T payload)
    {
        var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload,B07Contract.Wire));
        if(File.Exists(path)){if(!File.ReadAllBytes(path).SequenceEqual(bytes))throw new InvalidDataException("IDEMPOTENT_NATIVE_EVIDENCE_CHANGED");return;}
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);f.Write(bytes);f.Flush(true);
    }
}
