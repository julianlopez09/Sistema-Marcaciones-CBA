using System.Security.Principal;
using System.Text.Json;
using SistemaMarcaciones.Monthly.Controlled;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Monthly.Projects;

namespace SistemaMarcaciones.F13.Operational;

public sealed record LocalReviewPolicy(string Contract,string AllowedWindowsSid,string AuthorizationSHA256,
    string AuthorityId,bool AllowsLocalSourceReview,bool InstitutionalCertification);
public sealed record CoverageReviewDeclaration(string Reference,string Devices,string GenerationFilters,
    DateOnly From,DateOnly Until,bool BiometricComplete,bool DocumentarySearchComplete);

/// <summary>Explicit local operator review. Loading a file or writing a note cannot create this decision.</summary>
public sealed class LocalSourceReviewAuthority
{
    public const string Contract="F13_LOCAL_SOURCE_REVIEW_AUTHORITY_V1";
    readonly LocalReviewPolicy policy;
    readonly string policyHash;
    public string PolicySHA256=>policyHash;
    public LocalSourceReviewAuthority(string path,string expectedSHA256)
    {
        if(PhysicalMatrix.Hash(path)!=expectedSHA256)throw new InvalidDataException("La política de revisión cambió.");
        policy=JsonSerializer.Deserialize<LocalReviewPolicy>(File.ReadAllText(path))??throw new InvalidDataException("Falta la política de revisión.");
        if(policy.Contract!=Contract||policy.AuthorityId.Length==0||policy.AuthorizationSHA256.Length!=64
            ||!policy.AllowsLocalSourceReview||policy.InstitutionalCertification)
            throw new InvalidDataException("El alcance de la política de revisión es inválido.");
        policyHash=expectedSHA256;
    }
    public string RequireReviewer()
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        using var identity=WindowsIdentity.GetCurrent();
        if(identity.User?.Value!=policy.AllowedWindowsSid)
            throw new InvalidOperationException("Esta cuenta de Windows no está admitida como responsable de la revisión.");
        return identity.User.Value;
    }
    public SourceReview Review(string sourceSet,MonthlyPeriod period,CoverageReviewDeclaration declaration,
        string actor,bool automatedQa)
    {
        var sid=RequireReviewer();
        if(string.IsNullOrWhiteSpace(declaration.Reference)||string.IsNullOrWhiteSpace(declaration.Devices)
            ||string.IsNullOrWhiteSpace(declaration.GenerationFilters)||!declaration.BiometricComplete
            ||!declaration.DocumentarySearchComplete||declaration.From>period.Start||declaration.Until<period.End)
            throw new InvalidOperationException("Completa la referencia, los dispositivos, los filtros y el alcance verificado de las fuentes.");
        return new(sourceSet,actor,DateTimeOffset.UtcNow,
            "Verificación expresa de las fuentes del período y de la búsqueda documental dentro del alcance declarado.",
            declaration,sid,policyHash,policy.AuthorizationSHA256,policy.AuthorityId,
            automatedQa?"AUTOMATED_QA_NOT_HUMAN_ACCEPTANCE":"EXPLICIT_LOCAL_OPERATOR_REVIEW");
    }
}
