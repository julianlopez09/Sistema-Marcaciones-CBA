using System.Globalization;
using System.Text;
using System.Text.Json;
using SistemaMarcaciones.Monthly.Controlled;
using SistemaMarcaciones.Monthly.Export.Verified;
using SistemaMarcaciones.Monthly.Export.Auxiliary.Renderers;
using SistemaMarcaciones.Monthly.Projects;

namespace SistemaMarcaciones.F13.Operational;

/// <summary>Captures operands already returned by the pinned engines. It does not reevaluate a case.</summary>
public static class OperationalFacts
{
    public static string[] Applied(OperationalDay? day)=>day?.State=="COMPLETED"&&day.Trace.TryGetProperty("Decision",out var decision)
        ?decision.GetProperty("DocumentsApplied").EnumerateArray().Select(d=>d.GetString()!).ToArray():[];
    static JsonElement J(object value)=>JsonSerializer.SerializeToElement(value);
    static string T(JsonElement e,string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
    public static string Classification(string p)=>p switch{"ADMIN"=>"ADMINISTRATIVO","OPADMIN"=>"OPERATIVO_EN_LABORES_ADMINISTRATIVAS","DIRECTORES"=>"DIRECTOR",_=>"OPERATIVO"};
    public static string Strategy(string p)=>p switch{"OPADMIN"=>"OPERATIVO_ADMINISTRATIVO","DIRECTORES"=>"DIRECTOR","GUARDIA"=>"GUARDIA",_=>"ADMINISTRATIVO"};
    public static FactWitness Metadata(PersonnelRow p)=>new("PERSONNEL-"+p.SourceSHA256,p.SourceSHA256,"PERSONNEL_CSV/row="+p.SourceRow+"/record_id="+p.Id);
    public static async Task<VerifiedFact[]> Capture(OperationalSession s,CancellationToken ct=default)
    {
        var run=s.LastRun??throw new InvalidOperationException("Procesa el período.");
        if(!s.MatchesProfile(run))throw new InvalidOperationException("El resultado pertenece a otro perfil de revisión.");
        if(run.SourceSetSHA256!=await s.SourceSet(ct)||run.ReviewSHA256!=CanonicalMonthly.Hash(s.Review))throw new InvalidOperationException("El procesamiento no corresponde a las fuentes vigentes.");
        var coverage=await OperationalCoverage.Validate(s,ct);if(coverage==null)return [];
        var catalog=await s.Documents(ct);
        var facts=new List<VerifiedFact>();
        foreach(var day in run.Days.Where(d=>d.State=="COMPLETED"))
        {
            var person=s.People.Single(p=>p.Id==day.PersonId);var duty=s.Duties.Single(d=>d.PersonId==day.PersonId&&d.Date==day.Date);
            var decision=day.Trace.GetProperty("Decision");
            var selected=decision.GetProperty("MarksUsed").EnumerateArray().ToArray();
            var original=day.Trace.GetProperty("MarksEvaluated").EnumerateArray().ToArray();
            var documents=day.Trace.GetProperty("DocumentsExamined").Deserialize<DocumentRevision[]>()!;
            var applied=decision.GetProperty("DocumentsApplied").EnumerateArray().Select(d=>d.GetString()!).ToArray();
            var metadata=Metadata(person);var scheduleWitness=new FactWitness("DUTIES-"+duty.SourceSHA256,duty.SourceSHA256,"JORNADAS_CSV/row="+duty.SourceRow);
            var scopeWitness=new FactWitness("LOCAL-SOURCE-REVIEW-"+coverage.ReviewSHA256,coverage.ReviewSHA256,"source_reviews/"+coverage.ReviewSHA256+".json");
            var marks=selected.Select(m=>
            {
                var id=T(m,"MarcacionID");var source=original.Single(o=>T(o,"MarcacionID")==id);
                return new{Id=id,At=T(m,"At"),Witness=new FactWitness("HOST-BIO-"+T(source,"FuenteHash"),T(source,"FuenteHash"),T(source,"HojaFuente")+"/row="+source.GetProperty("FilaFuente").GetInt32())};
            }).ToArray();
            var docOperands=applied.Select(id=>
            {var d=documents.Single(d=>d.RecordId==id);return new{Id=id,Witness=new FactWitness("HOST-DOC-"+d.Data.Origin.SourceSHA256,d.Data.Origin.SourceSHA256,d.Data.Origin.SheetOrPage+"/"+d.Data.Origin.RowOrRecordLocator)};}).ToArray();
            var witnesses=new[]{metadata,scheduleWitness,scopeWitness}.Concat(marks.Select(m=>m.Witness)).Concat(docOperands.Select(d=>d.Witness)).Distinct().ToArray();
            SearchWitness? search=null;
            if(documents.Length==0)
            {
                var searchReport=new{Contract="CBA_EXACT_LOCAL_BOUNDED_DOCUMENT_SEARCH_V1",run.SourceSetSHA256,coverage.ReviewSHA256,day.PersonId,day.Date,
                    DocumentTypes=coverage.Coverage.Where(a=>a.SourceType=="DOCUMENT").SelectMany(a=>a.DocumentTypes).Distinct().Order().ToArray(),
                    ExactPerson=day.PersonId,ExactDate=day.Date,SourceCatalogFingerprint=CanonicalMonthly.Hash(catalog.Select(d=>new{d.RecordId,d.Version,d.FunctionalHash})),
                    DocumentarySnapshot=documents,Matches=0,Outcome="NO_MATCH_WITHIN_DECLARED_SCOPE",InstitutionalCertification=false};
                var text=CanonicalMonthly.Json(searchReport);var hash=CanonicalMonthly.Hash(searchReport);var file=Path.Combine(s.Root,"search_reports",hash+".json");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);if(!File.Exists(file)){using var f=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None);f.Write(Encoding.UTF8.GetBytes(text));f.Flush(true);}else if(File.ReadAllText(file)!=text)throw new InvalidDataException("El informe de búsqueda cambió.");
                var searched=catalog.Select(d=>new FactWitness("HOST-DOC-"+d.Data.Origin.SourceSHA256,d.Data.Origin.SourceSHA256,d.Data.Origin.SheetOrPage+"/"+d.Data.Origin.RowOrRecordLocator)).Append(scopeWitness).Distinct().ToArray();
                search=new(day.PersonId,day.Date,"Búsqueda documental local exacta, dentro del alcance declarado",searched,new("LOCAL-SEARCH-"+hash,hash,"search_reports/"+hash+".json"),"NO_MATCH_WITHIN_DECLARED_SCOPE");
            }
            var raw=day.Calculation.GetProperty("Resultados").EnumerateArray().ToArray();
            for(var i=0;i<day.Cases.Length;i++)
            {
                var c=day.Cases[i];var r=raw[i];
                var trace=J(new{Contract="ENGINE_TRACE_COMPLETE",ResultStableKey=run.Id+":"+day.PersonId+":"+day.Date.ToString("yyyy-MM-dd"),ResultIndex=i,
                    PersonId=day.PersonId,Date=day.Date.ToString("yyyy-MM-dd"),Category=T(r,"CategoriaNovedad"),
                    TemporalClassification=Classification(day.Population),TemporalClassificationWitness=metadata,
                    EffectiveSchedule=new{DutyStrategy=Strategy(day.Population),SelectionCapturedByEngine=true,Witness=scheduleWitness,
                        Description=day.Population=="GUARDIA"?"07:00 D a 07:00 D+1":day.Population=="DIRECTORES"?"480 minutos netos; almuerzo de 30 minutos":"08:00 a 16:30",ImportedDuty=duty},
                    Rule=c.Type,RuleVersion="CONTINUIDAD-20261001",Result=c.State,c.Minutes,Engine=day.Trace.GetProperty("Engine").GetString(),
                    SourceHashes=witnesses.Select(w=>w.SHA256).Distinct().ToArray(),Provenance=witnesses,
                    MarksCaptureState="CAPTURED_BY_ENGINE",DocumentsCaptureState="CAPTURED_BY_ENGINE",MarksActuallyUsed=marks,DocumentsActuallyApplied=docOperands,
                    SourceCoverage=coverage.Coverage,DecisionStages=new[]{new{Stage=day.Population=="GUARDIA"?"FROZEN_F5_PUBLIC_CONSUMER_COMPLETE":"FROZEN_F4_PUBLIC_CONSUMER_COMPLETE",CapturedInputSHA256=day.InputSHA256,CapturedOutputSHA256=CanonicalMonthly.Hash(day.Calculation),Decision=decision}},
                    ReviewScope=s.ExpectedReviewMode,InstitutionalCertification=false});
                var support=applied.Length>0?VerifiedSupport.DOCUMENT_APPLIED_EVIDENCE:search!=null?VerifiedSupport.DOCUMENT_NOT_LOCATED_WITH_SEARCH_WITNESS:VerifiedSupport.DOCUMENT_EXISTS_NOT_PROVEN_APPLIED;
                var fact=new VerifiedFact(trace,EvidenceLayer.NEW_ENGINE_RESULT,support,search,documents.Select(d=>d.RecordId).ToArray(),null,person.Position,metadata,c.State is "PENDIENTE_JUSTIFICACION" or "PENDIENTE_REGULARIZACION_DOCUMENTAL");
                fact.Validate();facts.Add(fact);
            }
        }
        return facts.ToArray();
    }
}
