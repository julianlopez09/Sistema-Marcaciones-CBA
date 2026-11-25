using System.Globalization;
using System.Text.Json;
using SistemaMarcaciones.F13.Operational;
using SistemaMarcaciones.Monthly.Controlled;
using SistemaMarcaciones.Monthly.Projects;

namespace SistemaMarcaciones.Desktop.Pilot.WinUI;

/// <summary>Presentation of captured operands; never applies a document or changes an engine result.</summary>
internal static class MonthlyPresentation
{
    public static bool MissingMark(string type)=>type is "SIN_MARCACIONES" or "MARCACION_FALTANTE"
        or "FALTA_INGRESO_MANANA" or "GUARDIA_SIN_MARCACIONES" or "GUARDIA_FALTA_MARCACION_INGRESO"
        or "GUARDIA_FALTA_MARCACION_SALIDA" or "FALTA_MARCACION_RETORNO_EVENTO" or "FALTA_MARCACION_SALIDA_EVENTO";
    public static string Minutes(OperationalCase value)=>MissingMark(value.Type)?"No determinado":value.Minutes?.ToString(CultureInfo.InvariantCulture)??"No determinado";
    public static string Case(string type)=>type switch
    {"GUARDIA_INGRESO_TARDIO"=>"Atraso al ingreso de Guardia","GUARDIA_SALIDA_ANTICIPADA"=>"Salida anticipada de Guardia",_=>OperationalLabels.Case(type)};
    public static string MatrixName(string path)=>Path.GetFileName(path) switch
    {var n when n.StartsWith("ADMIN_OPADMIN_",StringComparison.Ordinal)=>"Administrativos y operativos en labores administrativas",
     var n when n.StartsWith("DIRECTORES_",StringComparison.Ordinal)=>"Directores",
     var n when n.StartsWith("GUARDIA_",StringComparison.Ordinal)=>"Guardia operativa",_=>"Resumen del período y valores en revisión"};
    public static string SafeFileName(string value)
    {
        var invalid=Path.GetInvalidFileNameChars();var safe=new string(value.Select(c=>invalid.Contains(c)?'_':c).ToArray()).Trim(' ','.');
        return safe.Length<=180?safe:safe[..160]+"_"+CanonicalMonthly.Hash(value)[..12]+Path.GetExtension(safe);
    }
    public static string MemoFileName(string path,PersonnelRow[] people)
    {
        var person=people.SingleOrDefault(p=>Path.GetFileName(path)=="Borrador_"+p.Id+".docx");
        return person==null?Path.GetFileName(path):SafeFileName("Borrador - "+person.Name+".docx");
    }
    public static string PeriodCaption(OperationalMatrix matrix)
    {
        var dates=matrix.Bindings.Values.Where(b=>b.Date.HasValue).Select(b=>b.Date!.Value).Distinct().Order().ToArray();
        return MatrixName(matrix.Path)+(dates.Length==0?"":$" · {dates[0]:dd/MM/yyyy} — {dates[^1]:dd/MM/yyyy}");
    }
    public static (string Summary,string Technical) Document(MonthlyRun run,string personId,DateOnly date,string? exactDocumentId,string personName)
    {
        var day=run.Days.SingleOrDefault(d=>d.PersonId==personId&&d.Date==date);
        var captured=day!=null&&day.Trace.ValueKind==JsonValueKind.Object&&day.Trace.TryGetProperty("DocumentsExamined",out var examined)
            ?examined.Deserialize<DocumentRevision[]>()??[]:[];
        var found=exactDocumentId==null?[]:captured.Where(d=>d.RecordId==exactDocumentId).ToArray();
        var prefix=personName+" · "+date.ToString("dd/MM/yyyy",CultureInfo.InvariantCulture)+"\n\n";
        if(found.Length!=1)
            return(prefix+(found.Length>1?"La captura contiene varias versiones de ese respaldo. Se requiere resolver la vinculación exacta.":
                captured.Length>1?"Esta jornada tiene varios documentos examinados. La captura se conserva en el detalle; no se selecciona uno por inferencia.":
                "No hay un documento exacto vinculado a esta celda. Esto no acredita ausencia de documentos fuera del alcance revisado."),
                CanonicalMonthly.Json(new{Contract="CBA_CAPTURED_DOCUMENT_CONSULTATION_V1",run.Id,PersonId=personId,Date=date,ExactDocumentId=exactDocumentId,CapturedDocuments=captured}));
        var doc=found[0];var data=doc.Data;
        if(data.PersonId!=personId||data.Origin.SourceSHA256.Length!=64||string.IsNullOrWhiteSpace(data.Origin.RowOrRecordLocator))
            throw new InvalidDataException("La captura del documento no tiene una identidad y un origen exactos.");
        var capturedDay=day??throw new InvalidDataException("Falta la jornada exacta del documento capturado.");
        var hasDecision=capturedDay.State=="COMPLETED"&&capturedDay.Trace.TryGetProperty("Decision",out var decision)
            &&decision.TryGetProperty("DocumentsApplied",out var applied)&&applied.ValueKind==JsonValueKind.Array;
        var appliedByEngine=hasDecision&&capturedDay.Trace.GetProperty("Decision").GetProperty("DocumentsApplied").EnumerateArray().Any(d=>d.GetString()==doc.RecordId);
        var status=appliedByEngine?"Devuelto como aplicado por el motor C# en esta ejecución.":hasDecision?"No figura como aplicado en la decisión capturada del motor.":"La jornada no tiene una captura de aplicación completa.";
        var summary=prefix+status+$"\n\nDocumento: {data.OriginalNumber??"Sin número"}\n"+
            $"{data.Start:dd/MM/yyyy HH:mm} — {data.End:dd/MM/yyyy HH:mm}\n"+
            $"Aprobación declarada en la fuente: {data.ApprovalStatus}\nEntrega: {data.PhysicalDeliveryStatus}\nValidez declarada: {data.DocumentValidityStatus}\n"+
            $"Origen: {data.Origin.SourceFileName} · {data.Origin.SheetOrPage} / {data.Origin.RowOrRecordLocator}\nVersión capturada: {doc.Version}\n\n"+
            "Consulta de la evidencia de esta ejecución local. No aprueba documentos ni certifica la jornada laboral.";
        return(summary,CanonicalMonthly.Json(new{Contract="CBA_CAPTURED_DOCUMENT_CONSULTATION_V1",run.Id,run.SourceSetSHA256,run.ReviewSHA256,run.EvaluationProfile,
            PersonId=personId,Date=date,ExactDocument=doc,CapturedInputSHA256=capturedDay.InputSHA256,AppliedByCapturedEngine=appliedByEngine,InstitutionalCertification=false}));
    }
}
