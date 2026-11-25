using System.Text;
using SistemaMarcaciones.Monthly.Controlled;
using SistemaMarcaciones.Monthly.ForwardAdmission;
using SistemaMarcaciones.Monthly.SourceIngestion;
using SistemaMarcaciones.Monthly.Projects;

namespace SistemaMarcaciones.F13.Operational;

public sealed record OperationalCoverageReceipt(SourceCoverageAttestation[] Coverage,
    CoverageAuthorityDecision[] Decisions,MonthlySource[] Sources,string ReviewSHA256);

/// <summary>A new local review boundary; it does not claim institutional certification or historical equivalence.</summary>
public static class OperationalCoverage
{
    public static string PathFor(OperationalSession session,SourceReview review)=>
        Path.Combine(session.Root,"source_reviews",CanonicalMonthly.Hash(review)+".json");
    public static void Store(OperationalSession session,SourceReview review)
    {
        var path=PathFor(session,review);SistemaMarcaciones.Workspace.StorageV2.PathSafety.CheckLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes=Encoding.UTF8.GetBytes(CanonicalMonthly.Json(review));
        if(File.Exists(path)){if(!File.ReadAllBytes(path).SequenceEqual(bytes))throw new InvalidDataException("La revisión exacta cambió.");return;}
        using var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);f.Write(bytes);f.Flush(true);
    }
    public static async Task<OperationalCoverageReceipt?> Validate(OperationalSession session,CancellationToken ct=default)
    {
        if(!await session.ReviewCurrent(ct))return null;
        var review=session.Review!;var path=PathFor(session,review);
        if(!File.Exists(path)||File.ReadAllText(path)!=CanonicalMonthly.Json(review))throw new InvalidDataException("No se conserva la declaración de revisión exacta.");
        var hash=PhysicalMatrix.Hash(path);var id="LOCAL-SOURCE-REVIEW-"+hash;
        var w=new SourceProvenance(id,hash,"","source_reviews/"+Path.GetFileName(path),"F13_LOCAL_SOURCE_REVIEW_AUTHORITY_V1");
        var sources=session.Host.Sources.Select(s=>s.Source).Append(new MonthlySource(id,hash,"LOCAL_REVIEW",1,review.At,
            review.Declaration.From,review.Declaration.Until,"EXPLICIT_LOCAL_REVIEW",session.Period.Population,"SOURCE_IMPORTED",w.ParserVersion,[w],[])).ToArray();
        var people=session.People.Select(p=>p.Id).Order(StringComparer.Ordinal).ToArray();
        var scope=new List<SourceCoverageAttestation>();var decisions=new List<CoverageAuthorityDecision>();
        void Add(string artifact,string sha,string type,SourceProvenance evidence)
        {
            var decision="LOCAL-"+CanonicalMonthly.Hash(new{Review=hash,artifact,type});
            var a=new SourceCoverageAttestation(artifact,sha,type,review.Declaration.From,review.Declaration.Until,people,
                type=="BIOMETRIC"?review.Declaration.Devices.Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries):[],
                type=="DOCUMENT"?NewForwardInputGate.RequiredDocumentTypes:[],review.Declaration.GenerationFilters,
                CoverageStatus.COVERAGE_VERIFIED_COMPLETE,review.AuthorityId,decision,evidence,1);
            var d=new CoverageAuthorityDecision(decision,review.AuthorityId,SourceCoverageContract.ScopeHash(a),w);
            scope.Add(a);decisions.Add(d);
        }
        foreach(var source in session.Host.Sources)Add(source.Source.ArtifactId,source.Source.SHA256,"BIOMETRIC",source.Source.Provenance[0]);
        Add(id,hash,"DOCUMENT",w);
        MonthlySourceRegistry.Validate(sources);
        foreach(var a in scope)SourceCoverageContract.Validate(a,sources,decisions);
        return new(scope.ToArray(),decisions.ToArray(),sources,hash);
    }
}
