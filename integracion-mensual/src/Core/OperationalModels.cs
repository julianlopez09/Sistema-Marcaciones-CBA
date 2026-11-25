using System.Globalization;
using System.Text;
using SistemaMarcaciones.Monthly.Controlled;

namespace SistemaMarcaciones.F13.Operational;

public sealed record ImportedSource(string Kind,string FileName,string SHA256,int Rows);
public sealed record PersonnelRow(string Id,string Identity,string Name,string Position,string Population,
    DateOnly From,DateOnly Until,string SourceSHA256,int SourceRow,string? BiometricIdentity=null);
public sealed record DutyRow(string PersonId,DateOnly Date,DateTime Start,DateTime End,string Kind,
    string Platoon,string Reference,string State,string SourceSHA256,int SourceRow);
public sealed record SourceReview(string SourceSetSHA256,string Actor,DateTimeOffset At,string Statement,
    CoverageReviewDeclaration Declaration,string ReviewerWindowsSid,string PolicySHA256,string AuthorizationSHA256,
    string AuthorityId,string ReviewMode);
public sealed record OperationalCase(string Id,string PersonId,DateOnly Date,string Type,string State,
    decimal? Minutes,string[] Marks,string[] AppliedDocuments,string Description);
public sealed record OperationalDay(string PersonId,DateOnly Date,string Population,string State,
    string? Value,OperationalCase[] Cases,string InputSHA256,System.Text.Json.JsonElement Calculation,
    System.Text.Json.JsonElement Trace,string[] Gaps);
public sealed record MonthlyRun(string Id,string SourceSetSHA256,string ReviewSHA256,DateTimeOffset At,OperationalDay[] Days,string? EvaluationProfile=null);
public sealed record LocalExport(string Id,string Kind,string Path,string SHA256,int Revision);

public static class OperationalLabels
{
    public static string PopulationCode(string value)=>value.Trim().ToUpperInvariant() switch
    {"ADMIN" or "ADMINISTRATIVO" or "ADMINISTRATIVOS"=>"ADMIN","OPADMIN" or "OPERATIVO ADMINISTRATIVO" or "OPERATIVOS EN LABORES ADMINISTRATIVAS"=>"OPADMIN","DIRECTOR" or "DIRECTORES"=>"DIRECTORES","GUARDIA" or "GUARDIA OPERATIVA"=>"GUARDIA",_=>throw new InvalidDataException("Población no reconocida. Utiliza Administrativo, Operativo administrativo, Director o Guardia.")};
    public static string Population(string value)=>value switch
    {
        "ADMIN"=>"Administrativos",
        "OPADMIN"=>"Operativos en labores administrativas",
        "DIRECTORES"=>"Directores",
        "GUARDIA"=>"Guardia operativa",
        _=>value
    };
    public static string Case(string value)=>value switch
    {
        "ATRASO_INGRESO"=>"Atraso al ingreso",
        "ALMUERZO_DURACION_INCORRECTA"=>"Exceso en el tiempo de almuerzo",
        "FALTA_INGRESO_MANANA"=>"Falta de marcación de ingreso",
        "MARCACION_FALTANTE"=>"Falta de marcación",
        "SIN_MARCACIONES"=>"Sin marcaciones acreditadas",
        "SALIDA_ANTICIPADA"=>"Salida anticipada",
        "SALIDA_ANTICIPADA_EVENTO"=>"Salida anterior al intervalo autorizado",
        "RETORNO_TARDIO_EVENTO"=>"Retorno posterior al intervalo autorizado",
        "PENDIENTE_JUSTIFICACION"=>"Pendiente de justificación",
        "PENDIENTE_REGULARIZACION_DOCUMENTAL"=>"Pendiente de regularización documental",
        "COMPLETED"=>"Procesado",
        "PENDING_EVIDENCE"=>"Falta verificar evidencia",
        _=>value.Replace('_',' ')
    };
}

public sealed record ImportRow(int Number,IReadOnlyDictionary<string,string> Cells);

public static class OperationalCsv
{
    public static ImportRow[] Read(string path,string[] required)
    {
        string text;
        try{text=File.ReadAllText(path,new UTF8Encoding(false,true)).TrimStart('\uFEFF');}
        catch(DecoderFallbackException e){throw new InvalidDataException("Guarda el archivo como CSV UTF-8 para conservar nombres y encabezados sin alteraciones.",e);}
        var first=text.Split('\n',2)[0];
        var separator=first.Count(c=>c==';')>first.Count(c=>c==',')?';':',';
        var records=Parse(text,separator);
        if(records.Count==0)throw new InvalidDataException("El archivo está vacío.");
        var headers=records[0].Select(c=>c.Trim()).ToArray();
        if(headers.Distinct(StringComparer.Ordinal).Count()!=headers.Length)
            throw new InvalidDataException("El archivo contiene encabezados repetidos.");
        var missing=required.Except(headers,StringComparer.Ordinal).ToArray();
        if(missing.Length>0)throw new InvalidDataException("Faltan columnas requeridas. "+string.Join(", ",missing));
        return records.Skip(1).Select((r,i)=>
        {
            if(r.Length!=headers.Length)throw new InvalidDataException("La fila "+(i+2)+" tiene una cantidad de columnas incorrecta.");
            return new ImportRow(i+2,headers.Zip(r).ToDictionary(p=>p.First,p=>p.Second.Trim(),StringComparer.Ordinal));
        }).ToArray();
    }
    static List<string[]> Parse(string text,char separator)
    {
        var result=new List<string[]>();
        var row=new List<string>();
        var cell=new StringBuilder();
        var quoted=false;var quoteClosed=false;
        for(var i=0;i<text.Length;i++)
        {
            var c=text[i];
            if(c=='"')
            {
                if(quoted&&i+1<text.Length&&text[i+1]=='"'){cell.Append('"');i++;}
                else if(quoted){quoted=false;quoteClosed=true;}
                else if(cell.Length==0&&!quoteClosed)quoted=true;
                else throw new InvalidDataException("El CSV contiene comillas mal colocadas. Encierra el campo completo entre comillas y duplica las comillas internas.");
            }
            else if(c==separator&&!quoted){row.Add(cell.ToString());cell.Clear();quoteClosed=false;}
            else if((c=='\r'||c=='\n')&&!quoted)
            {
                if(c=='\r'&&i+1<text.Length&&text[i+1]=='\n')i++;
                row.Add(cell.ToString());cell.Clear();
                if(row.Any(v=>v.Length>0))result.Add(row.ToArray());
                row.Clear();quoteClosed=false;
            }
            else if(quoteClosed)throw new InvalidDataException("El CSV contiene texto después de una comilla de cierre. Corrige ese campo antes de importarlo.");
            else cell.Append(c);
        }
        if(quoted)throw new InvalidDataException("Hay comillas sin cerrar en el archivo.");
        if(cell.Length>0||row.Count>0){row.Add(cell.ToString());result.Add(row.ToArray());}
        return result;
    }
    public static DateOnly Date(string value)=>DateOnly.TryParseExact(value,
        new[]{"dd/MM/yyyy","yyyy-MM-dd"},CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)
        ?date:throw new InvalidDataException("Fecha inválida. Utiliza dd/MM/aaaa.");
    public static DateTime Stamp(string value)=>DateTime.TryParseExact(value,
        new[]{"dd/MM/yyyy HH:mm","yyyy-MM-dd HH:mm","yyyy-MM-dd'T'HH:mm:ss"},
        CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)
        ?DateTime.SpecifyKind(date,DateTimeKind.Unspecified):throw new InvalidDataException("Fecha y hora inválidas. Utiliza dd/MM/aaaa HH:mm.");
}
