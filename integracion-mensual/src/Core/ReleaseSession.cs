using System.Text.Json;
using SistemaMarcaciones.Monthly.Controlled;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Monthly.ForwardAttendance;
using SistemaMarcaciones.Monthly.Host.NativeIntegration;
using SistemaMarcaciones.Monthly.Host.Wiring;
using SistemaMarcaciones.Monthly.Management;
using SistemaMarcaciones.Monthly.Native.Outcomes;
using SistemaMarcaciones.Monthly.Projects;
using SistemaMarcaciones.Centro.Presentation;
using SistemaMarcaciones.Workspace.StorageV2;

namespace SistemaMarcaciones.F13.Integrated.Native;

public sealed record ReleaseConfiguration(string Contract,string AuthoritySHA256,B09File BaseConfiguration,
    string ImplementationSHA256,B09File Stencil,B09File? BiometricSource,string OutputRoot,bool TestFixtureOnly);
public sealed record ReleaseCatalogEntry(string Name,B09File Configuration);
public sealed record ReleaseCatalog(string Contract,ReleaseCatalogEntry[] Entries);

public sealed class ReleaseSession
{
    public const string Contract="F13_NATIVE_ADMIN_OPADMIN_COMPLETION_RC04_V1";
    public const string Authority="639725ECA0014A9F6790C7A2EDF932D0E9D59138372BDBFBCE4FF0E3E7932E38";
    public static string ImplementationSHA256=>PhysicalMatrix.Hash(typeof(ReleaseSession).Assembly.Location);
    public B09Session Inner {get;}
    public ReleaseConfiguration Configuration {get;}
    public NativeAttendanceCoordinator Attendance {get;}
    ReleaseSession(B09Session inner,ReleaseConfiguration configuration){Inner=inner;Configuration=configuration;Attendance=new(inner);}
    static readonly HashSet<string> AddedKinds=new(StringComparer.Ordinal)
    {
        "NEW_FUNCTIONAL_VERSION",MonthlyHostCommands.NativeExecutionKind,B07Contract.Event,B09Session.DocumentaryKind,
        NativeAttendanceCoordinator.Contract,NativeAttendanceCoordinator.ExportKind,MatrixReview.DefinitionKind,
        MatrixReview.EditKind,MatrixReview.ProposalKind,MatrixReview.ExportKind,ProjectCoordinator.RangeKind,
        ManualCellReview.Kind,ManualRangeReview.Kind,ManualWorkbookExport.Kind,ManualPresentation.Contract,
        ManagementCenter.EventKind,CentroViewModel.ProposalEventKind,TypedLateReview.Kind,WorkingRowsExport.Kind,CompletionExports.Kind
    };
    public static async Task<ReleaseSession> AdmitInitial(B09File baseConfiguration,B09File stencil,B09File? biometric,string output,CancellationToken ct=default)
    {
        // Initial admission is performed by the frozen B09 bootstrap, before the new event contract exists.
        var inner=await B09Bootstrap.Open(baseConfiguration,B09Route.NativeFixtureSandbox,ct);
        if(inner.Host.Executions.Length>0)throw new InvalidDataException("NATIVE_RELEASE_CANNOT_PROMOTE_HISTORICAL_RESULTS");
        var c=new ReleaseConfiguration(Contract,Authority,baseConfiguration,ImplementationSHA256,stencil,biometric,B09Contract.Sandbox(output),true);
        ValidateConfiguration(c);return new(inner,c);
    }
    static void ValidateConfiguration(ReleaseConfiguration c)
    {
        if(c.Contract!=Contract||c.AuthoritySHA256!=Authority||!c.TestFixtureOnly||c.ImplementationSHA256!=ImplementationSHA256)
            throw new InvalidDataException("EXACT_ADDITIVE_NATIVE_RELEASE_CONFIGURATION_REQUIRED");
        _=B09Contract.Sandbox(c.OutputRoot);
        if(c.Stencil.SHA256!=AttendanceWorkbook.AcceptedAnonymousStencilSHA||PhysicalMatrix.Hash(c.Stencil.Path)!=c.Stencil.SHA256)
            throw new InvalidDataException("EXACT_ACCEPTED_STENCIL_REQUIRED");
        if(c.BiometricSource!=null&&PhysicalMatrix.Hash(c.BiometricSource.Path)!=c.BiometricSource.SHA256)
            throw new InvalidDataException("QA_BIOMETRIC_SOURCE_CHANGED");
    }
    public static async Task<ReleaseSession> Open(B09File configuration,CancellationToken ct=default)
    {
        var release=configuration.Read<ReleaseConfiguration>();ValidateConfiguration(release);
        var c=release.BaseConfiguration.Read<B09Configuration>();var witness=c.Witness.Read<B09Witness>();
        if(c.Contract!=B09Contract.Version||c.Route!=B09Route.NativeFixtureSandbox||!c.ExplicitNativeOptIn||!c.TestFixtureOnly
            ||c.AuthoritySHA256!=B09Contract.Authority||c.ImplementationSHA256!=B09Contract.ImplementationSHA256||c.PopulationContract!=B09Contract.Population)
            throw new InvalidDataException("FROZEN_B09_BASE_CONFIGURATION_REQUIRED");
        var root=B09Contract.Sandbox(c.ProjectRoot);c.Period.Validate();
        if(witness.Contract!=B09Contract.Version||Path.GetFullPath(witness.ProjectRoot)!=root||witness.ProjectId!=c.ProjectId||witness.Period!=c.Period
            ||c.ProjectId!=MonthlyProjectHub.Identity(c.Period)||witness.People.Length==0
            ||witness.People.Distinct(StringComparer.Ordinal).Count()!=witness.People.Length
            ||witness.People.Any(p=>!p.StartsWith("SRV-QA-",StringComparison.Ordinal))
            ||c.ExactIds.Values.Any(p=>!witness.People.Contains(p,StringComparer.Ordinal)))
            throw new InvalidDataException("ADDITIVE_RELEASE_EXACT_ANONYMOUS_PROJECT_SCOPE");
        foreach(var file in c.Inputs)_=file.Read<JsonElement>();
        foreach(var part in new[]{"project_registry","shared_documents",Path.Combine("projects",c.ProjectId)})
            if(!File.Exists(Path.Combine(root,part,"manifest.json")))throw new InvalidDataException("EXISTING_ADMITTED_WORKSPACE_REQUIRED");
        var coordinator=new ProjectCoordinator(root);var h=await coordinator.Open(c.ProjectId,ct);var w=h.Work;
        if(h.Service.ActiveProject!.Period!=c.Period||w.SessionId!=c.ProjectId||w.Revision<witness.Revision||w.Versions.Count<witness.VersionCount
            ||witness.VersionCount<1||w.Events.Count<witness.EventCount||witness.HistoricalExecutionHashes.Length!=0)
            throw new InvalidDataException("NATIVE_RELEASE_BASELINE_SCOPE");
        if(CanonicalMonthly.Hash(w.Versions.Take(witness.VersionCount).ToArray())!=witness.VersionSetSHA256
            ||CanonicalMonthly.Hash(w.Events.Take(witness.EventCount).ToArray())!=witness.EventsSHA256)
            throw new InvalidDataException("NATIVE_RELEASE_FROZEN_PREFIX_CHANGED");
        if(w.Events.Skip(witness.EventCount).Any(e=>!AddedKinds.Contains(e.Kind)))throw new InvalidDataException("UNKNOWN_ADDITIVE_EVENT_REQUIRES_CONTRACT");
        foreach(var version in w.Versions)
            if(version.Period!=c.Period||CanonicalMonthly.VersionHash(version.Period,version.Sources,version.RuleVersion,version.Records)!=version.FunctionalHash)
                throw new InvalidDataException("NATIVE_RELEASE_PIPELINE_HASH_CHANGED");
        if(w.Versions.Skip(witness.VersionCount).Any(v=>v.RuleVersion is not (B09Contract.HostRule or NativeAcceptedForwardConsumer.AcceptedRule)))
            throw new InvalidDataException("NATIVE_RELEASE_UNKNOWN_RULE");
        foreach(var pair in witness.BaselineRecords)
            if(w.Current.Records.GetValueOrDefault(pair.Key)!=pair.Value)throw new InvalidDataException("NATIVE_RELEASE_BASELINE_RECORD_CHANGED");
        foreach(var key in w.Current.Records.Keys.Except(witness.BaselineRecords.Keys,StringComparer.Ordinal))
            if(!key.StartsWith(B07Contract.InputPrefix,StringComparison.Ordinal)&&!key.StartsWith(B07Contract.ResultPrefix,StringComparison.Ordinal)&&!key.StartsWith("B09_RESULT:",StringComparison.Ordinal))
                throw new InvalidDataException("NATIVE_RELEASE_UNADMITTED_RECORD");
        var host=new MonthlyHostCommands(coordinator,h,c.ExactIds);if(host.Executions.Length!=0)throw new InvalidDataException("HISTORICAL_HOST_EXECUTION_NOT_NATIVE_RELEASE");
        StreamingSemanticCertifierV2.Certify(h.Service.ActiveRepository!.PhysicalSnapshot());
        var bootstrap=new B09BootstrapReceipt("ADDITIVE_NATIVE_RELEASE_RESUME",h.Id,c.Period,w.Revision,w.Versions.Count,w.Current.Records.Count,
            h.Service.ActiveRepository.PhysicalSnapshot().Checksum,CanonicalMonthly.Hash(w.Versions),0,null);
        var inner=new B09Session(c,witness,coordinator,h,host,new ForwardAttendanceCoordinator(coordinator,h),bootstrap);
        var result=new ReleaseSession(inner,release);_=NativeAttendanceCoordinator.Collect(inner);
        foreach(var r in result.Attendance.Registrations)NativeAttendanceCoordinator.Verify(r);
        // This is an explicitly different admission contract, NOT a widening of frozen B09Bootstrap.
        return result;
    }
    public B09File SaveConfiguration(string name="integrated_release_configuration.json")
    {
        var file=Path.Combine(Inner.Configuration.ProjectRoot,name);NativeAttendanceCoordinator.WriteExact(file,Configuration);return new(file,PhysicalMatrix.Hash(file));
    }
    public async Task ReimportExactQaBiometrics(string actor,DateTimeOffset at,CancellationToken ct=default)
    {
        var source=Configuration.BiometricSource??throw new InvalidDataException("NO_ADMITTED_PHYSICAL_BIOMETRIC_XLSX_FOR_THIS_PROFILE");
        if(PhysicalMatrix.Hash(source.Path)!=source.SHA256)throw new InvalidDataException("ADMITTED_BIOMETRIC_SOURCE_CHANGED");
        await Inner.Host.ImportBiometrics(source.Path,await Inner.Host.PreviewBiometrics(source.Path,ct),actor,at,ct);
    }
    public async Task ExecutePinnedInputs(B09File freshMedium,string actor,DateTimeOffset at,CancellationToken ct=default)
    {
        B09Contract.Medium(freshMedium);
        foreach(var pin in Inner.Configuration.Inputs)
        {
            ct.ThrowIfCancellationRequested();var raw=pin.Read<JsonElement>();
            if(raw.TryGetProperty("Native",out _))_=await Inner.RunOutcome(pin,freshMedium,Inner.Handle.Work.Revision,actor,at,ct);
            else if(raw.TryGetProperty("Payloads",out _))_=await Inner.RunAcceptedHost(pin,freshMedium,Inner.Handle.Work.Revision,actor,at,ct);
            else _=await Inner.RunDocumentary(pin,freshMedium,Inner.Handle.Work.Revision,actor,at,ct);
        }
    }
    public Task<NativeMatrixRegistration> Generate(string actor,DateTimeOffset at,CancellationToken ct=default)=>
        Attendance.Generate(Configuration.Stencil.Path,Configuration.OutputRoot,Inner.Handle.Work.Revision,actor,at,ct);
}
