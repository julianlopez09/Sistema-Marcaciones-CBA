using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using SistemaMarcaciones.F13.Integrated.Native;
using SistemaMarcaciones.Monthly.Controlled;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Monthly.Projects;
using SistemaMarcaciones.Workspace.StorageV2;

namespace SistemaMarcaciones.F13.Operational;

public sealed record OperationalMatrix(string Id,string Path,string SHA256,string RunId,
    string SourceSetSHA256,IReadOnlyDictionary<string,CellBinding> Bindings);

/// <summary>Local working artifacts. No new attendance rules, monetary calculation, signatures or institutional publication.</summary>
public static class OperationalArtifacts
{
    public const string MatrixKind="CBA_MONTHLY_WORKING_MATRIX_V1";
    static readonly XNamespace S="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace R="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace P="http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace W="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly string[] Populations=["ADMIN","OPADMIN","DIRECTORES","GUARDIA"];
    static byte[] Xml(XDocument d)=>Encoding.UTF8.GetBytes(d.ToString(SaveOptions.DisableFormatting));
    static XElement C(int row,int column,string? value,int style=0,bool numeric=false)
    {
        var c=new XElement(S+"c",new XAttribute("r",MatrixCoordinates.A1(row,column)),new XAttribute("s",style));
        if(value==null)return c;
        if(numeric){c.Add(new XElement(S+"v",value));return c;}
        c.SetAttributeValue("t","inlineStr");c.Add(new XElement(S+"is",new XElement(S+"t",new XAttribute(XNamespace.Xml+"space","preserve"),value)));return c;
    }
    internal static void WritePackage(string path,IReadOnlyDictionary<string,byte[]> parts)
    {
        PathSafety.CheckLinks(path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var memory=new MemoryStream();
        using(var z=new ZipArchive(memory,ZipArchiveMode.Create,true))foreach(var p in parts.OrderBy(p=>p.Key,StringComparer.Ordinal))
        {var e=z.CreateEntry(p.Key,CompressionLevel.Optimal);e.LastWriteTime=new(2000,1,1,0,0,0,TimeSpan.Zero);using var f=e.Open();f.Write(p.Value);}
        var bytes=memory.ToArray();
        if(File.Exists(path)){if(!File.ReadAllBytes(path).SequenceEqual(bytes))throw new InvalidDataException("El artefacto guardado cambió. Se requiere otra identidad.");return;}
        using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);output.Write(bytes);output.Flush(true);
    }
    static XDocument Styles()
    {
        var fills=new[]{"00000000","FFEDF2F7","FF96CC52","FFDDEBF7","FFD9B8E8","FFF4B183","FFFF0000"};
        var styles=new XElement(S+"cellXfs",new XAttribute("count",8),
            Enumerable.Range(0,8).Select(i=>new XElement(S+"xf",new XAttribute("numFmtId",0),new XAttribute("fontId",i==1?1:0),
                new XAttribute("fillId",i==0?0:i==1?1:i-1),new XAttribute("borderId",0),new XAttribute("xfId",0),new XAttribute("applyAlignment",1),
                new XElement(S+"alignment",new XAttribute("vertical","center"),new XAttribute("wrapText",1),new XAttribute("horizontal",i==1?"center":"left")))));
        return new(new XElement(S+"styleSheet",new XElement(S+"fonts",new XAttribute("count",2),
            new XElement(S+"font",new XElement(S+"sz",new XAttribute("val",11)),new XElement(S+"name",new XAttribute("val","Calibri"))),
            new XElement(S+"font",new XElement(S+"b"),new XElement(S+"sz",new XAttribute("val",11)),new XElement(S+"name",new XAttribute("val","Calibri")))),
            new XElement(S+"fills",new XAttribute("count",fills.Length),fills.Select((fill,i)=>new XElement(S+"fill",i==0?new XElement(S+"patternFill",new XAttribute("patternType","none")):
                new XElement(S+"patternFill",new XAttribute("patternType","solid"),new XElement(S+"fgColor",new XAttribute("rgb",fill)),new XElement(S+"bgColor",new XAttribute("indexed",64)))))),
            new XElement(S+"borders",new XAttribute("count",1),new XElement(S+"border",new XElement(S+"left"),new XElement(S+"right"),new XElement(S+"top"),new XElement(S+"bottom"),new XElement(S+"diagonal"))),
            new XElement(S+"cellStyleXfs",new XAttribute("count",1),new XElement(S+"xf",new XAttribute("numFmtId",0),new XAttribute("fontId",0),new XAttribute("fillId",0),new XAttribute("borderId",0))),styles,
            new XElement(S+"cellStyles",new XAttribute("count",1),new XElement(S+"cellStyle",new XAttribute("name","Normal"),new XAttribute("xfId",0),new XAttribute("builtinId",0)))));
    }
    static XDocument Sheet(string title,string[] headers,IReadOnlyList<string?[]> rows,int frozenColumns=1,IReadOnlyDictionary<(int Row,int Column),int>? styles=null)
    {
        var root=new XElement(S+"worksheet",new XAttribute(XNamespace.Xmlns+"r",R),
            new XElement(S+"dimension",new XAttribute("ref","A1:"+MatrixCoordinates.A1(rows.Count+3,headers.Length))),
            new XElement(S+"sheetViews",new XElement(S+"sheetView",new XAttribute("workbookViewId",0),
                new XElement(S+"pane",new XAttribute("xSplit",frozenColumns),new XAttribute("ySplit",3),new XAttribute("topLeftCell",MatrixCoordinates.A1(4,frozenColumns+1)),new XAttribute("activePane","bottomRight"),new XAttribute("state","frozen")))),
            new XElement(S+"sheetFormatPr",new XAttribute("defaultRowHeight",32)),
            new XElement(S+"cols",Enumerable.Range(1,headers.Length).Select(i=>new XElement(S+"col",new XAttribute("min",i),new XAttribute("max",i),new XAttribute("width",i==1?32:18),new XAttribute("customWidth",1)))));
        var data=new XElement(S+"sheetData",new XElement(S+"row",new XAttribute("r",1),new XAttribute("ht",30),new XAttribute("customHeight",1),C(1,1,title,1)),
            new XElement(S+"row",new XAttribute("r",2),C(2,1,"REVISIÓN LOCAL · No certificada · Sin cálculo de descuentos")),
            new XElement(S+"row",new XAttribute("r",3),headers.Select((h,i)=>C(3,i+1,h,1))));
        for(var index=0;index<rows.Count;index++)
        {var row=index+4;data.Add(new XElement(S+"row",new XAttribute("r",row),new XAttribute("ht",48),new XAttribute("customHeight",1),rows[index].Select((v,i)=>C(row,i+1,v,styles?.GetValueOrDefault((row,i+1))??0))));}
        root.Add(data);root.Add(new XElement(S+"mergeCells",new XAttribute("count",2),new XElement(S+"mergeCell",new XAttribute("ref","A1:"+MatrixCoordinates.A1(1,headers.Length))),new XElement(S+"mergeCell",new XAttribute("ref","A2:"+MatrixCoordinates.A1(2,headers.Length)))));
        root.Add(new XElement(S+"pageMargins",new XAttribute("left",0.25),new XAttribute("right",0.25),new XAttribute("top",0.5),new XAttribute("bottom",0.5),new XAttribute("header",0.2),new XAttribute("footer",0.2)));
        return new(root);
    }
    public static OperationalMatrix[] Matrices(OperationalSession s)=>s.Handle==null?[]:ProjectEvents.Read<OperationalMatrix>(s.Handle.Work,MatrixKind).ToArray();
    static async Task<MonthlyRun> Current(OperationalSession s,CancellationToken ct)
    {
        var run=s.LastRun??throw new InvalidOperationException("Procesa el mes antes de generar documentos.");
        if(!s.MatchesProfile(run))throw new InvalidOperationException("El resultado pertenece a otro perfil de revisión. No puede promoverse una prueba a uso institucional.");
        if(run.SourceSetSHA256!=await s.SourceSet(ct)||run.ReviewSHA256!=CanonicalMonthly.Hash(s.Review))throw new InvalidOperationException("Las fuentes o su revisión cambiaron. Procesa nuevamente el período.");
        return run;
    }
    public static WorkbookProfile Profile(OperationalSession s,OperationalMatrix matrix)
    {
        if(PhysicalMatrix.Hash(matrix.Path)!=matrix.SHA256)throw new InvalidDataException("La matriz guardada cambió.");
        return WorkbookProfiles.Read(matrix.Path,matrix.SHA256,MatrixKind,s.Period,synthetic:false,bindings:matrix.Bindings);
    }
    public static async Task<OperationalMatrix> Generate(OperationalSession s,CancellationToken ct=default)
    {
        var physical=await OperationalPhysical.Generate(s,ct);await GenerateSummary(s,ct);
        return physical.FirstOrDefault()??throw new InvalidOperationException("El período no tiene personal.");
    }
    public static async Task<OperationalMatrix> GenerateSummary(OperationalSession s,CancellationToken ct=default)
    {
        var run=await Current(s,ct);var prior=Matrices(s).LastOrDefault(m=>m.Id==MatrixKind+":"+run.Id);if(prior!=null){Profile(s,prior);return prior;}
        var sheets=new List<(string Name,XDocument Document)>();var bindings=new Dictionary<string,CellBinding>(StringComparer.Ordinal);
        foreach(var population in Populations)
        {
            var cut=s.PeriodFor(population);
            var dates=Enumerable.Range(0,cut.End.DayNumber-cut.Start.DayNumber+1).Select(i=>cut.Start.AddDays(i)).ToArray();
            var people=s.People.Where(p=>p.Population==population).OrderBy(p=>p.Name,StringComparer.Ordinal).ToArray();
            var headers=new[]{"Servidor","Cargo"}.Concat(dates.Select(d=>d.ToString("dd/MM/yyyy"))).Append("Observaciones").ToArray();
            var rows=new List<string?[]>();var styles=new Dictionary<(int,int),int>();
            for(var i=0;i<people.Length;i++)
            {
                var person=people[i];var values=new List<string?>{person.Name,person.Position};
                for(var j=0;j<dates.Length;j++)
                {
                    var day=run.Days.SingleOrDefault(d=>d.PersonId==person.Id&&d.Date==dates[j]);
                    values.Add(day?.State=="COMPLETED"?day.Value:"EVIDENCIA INSUFICIENTE");
                    var docs=OperationalFacts.Applied(day);
                    bindings[MatrixReview.Key(population,MatrixCoordinates.A1(i+4,j+3))]=new(person.Id,dates[j],day?.Cases.Length==1?day.Cases[0].Id:null,docs.Length==1?docs[0]:null,MatrixKind+":"+run.Id);
                    styles[(i+4,j+3)]=day?.Value switch{"PERMISO PERSONAL"=>3,"PERMISO MÉDICO"=>4,"LICENCIA"=>5,"VACACIONES"=>6,_=>day?.State=="COMPLETED"&&decimal.TryParse(day.Value,NumberStyles.Number,CultureInfo.InvariantCulture,out var n)&&n>0?7:0};
                }
                values.Add("");rows.Add(values.ToArray());
            }
            sheets.Add((population,Sheet(OperationalLabels.Population(population)+$" · {cut.Start:dd/MM/yyyy} a {cut.End:dd/MM/yyyy}",headers,rows,2,styles)));
        }
        var cases=run.Days.SelectMany(d=>d.Cases).ToArray();
        string Name(string id)=>s.People.Single(p=>p.Id==id).Name;
        sheets.Add(("Novedades",Sheet("Novedades y sus respaldos",["Servidor","Fecha","Novedad","Minutos","Marcaciones","Documentos aplicados","Detalle"],
            cases.Select(c=>new string?[]{Name(c.PersonId),c.Date.ToString("dd/MM/yyyy"),OperationalLabels.Case(c.Type),c.Minutes?.ToString(CultureInfo.InvariantCulture)??"No determinado",string.Join(" · ",c.Marks),string.Join(" · ",c.AppliedDocuments),c.Description}).ToArray())));
        sheets.Add(("Valores en revisión",Sheet("Valores en revisión · sin nuevo cálculo",["Servidor","Fechas revisadas","Minutos conocidos","Importe","Estado"],
            run.Days.Where(d=>d.State=="COMPLETED"&&d.Cases.Any(c=>c.Minutes>0)).GroupBy(d=>d.PersonId).Select(g=>new string?[]{Name(g.Key),string.Join("; ",g.Select(d=>d.Date.ToString("dd/MM/yyyy"))),g.SelectMany(d=>d.Cases).Where(c=>c.Minutes.HasValue).Sum(c=>c.Minutes!.Value).ToString(CultureInfo.InvariantCulture),"No determinado","Sin cálculo D08; pendiente de acreditación"}).ToArray())));
        var wb=new XDocument(new XElement(S+"workbook",new XAttribute(XNamespace.Xmlns+"r",R),new XElement(S+"sheets",sheets.Select((sheet,i)=>new XElement(S+"sheet",new XAttribute("name",sheet.Name),new XAttribute("sheetId",i+1),new XAttribute(R+"id","rId"+(i+1))))),new XElement(S+"calcPr",new XAttribute("calcMode","manual"))));
        WorkbookViewCompatibility.EnsurePrimaryView(wb);
        var parts=new Dictionary<string,byte[]>(StringComparer.Ordinal){["xl/workbook.xml"]=Xml(wb),["xl/styles.xml"]=Xml(Styles()),
            ["_rels/.rels"]=Xml(new(new XElement(P+"Relationships",new XElement(P+"Relationship",new XAttribute("Id","rId1"),new XAttribute("Type",R.NamespaceName+"/officeDocument"),new XAttribute("Target","xl/workbook.xml")))))};
        var relationships=new XElement(P+"Relationships",sheets.Select((sheet,i)=>new XElement(P+"Relationship",new XAttribute("Id","rId"+(i+1)),new XAttribute("Type",R.NamespaceName+"/worksheet"),new XAttribute("Target","worksheets/sheet"+(i+1)+".xml"))));
        relationships.Add(new XElement(P+"Relationship",new XAttribute("Id","styles"),new XAttribute("Type",R.NamespaceName+"/styles"),new XAttribute("Target","styles.xml")));parts["xl/_rels/workbook.xml.rels"]=Xml(new(relationships));
        XNamespace types="http://schemas.openxmlformats.org/package/2006/content-types";
        var content=new XElement(types+"Types",new XElement(types+"Default",new XAttribute("Extension","rels"),new XAttribute("ContentType","application/vnd.openxmlformats-package.relationships+xml")),new XElement(types+"Default",new XAttribute("Extension","xml"),new XAttribute("ContentType","application/xml")),new XElement(types+"Override",new XAttribute("PartName","/xl/workbook.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),new XElement(types+"Override",new XAttribute("PartName","/xl/styles.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")));
        for(var i=0;i<sheets.Count;i++){parts["xl/worksheets/sheet"+(i+1)+".xml"]=Xml(sheets[i].Document);content.Add(new XElement(types+"Override",new XAttribute("PartName","/xl/worksheets/sheet"+(i+1)+".xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));}
        parts["[Content_Types].xml"]=Xml(new(content));
        var file=Path.Combine(s.Root,"matrices",s.Handle!.Id,run.Id,"Matrices_"+s.PeriodFor("DIRECTORES").Start.ToString("yyyy_MM")+"_revision_local.xlsx");
        ct.ThrowIfCancellationRequested();WritePackage(file,parts);
        var matrix=new OperationalMatrix(MatrixKind+":"+run.Id,file,PhysicalMatrix.Hash(file),run.Id,run.SourceSetSHA256,bindings);
        var profile=Profile(s,matrix);await s.Coordinator.Add(s.Handle,profile,s.Actor,DateTimeOffset.UtcNow,ct);await s.Event(MatrixKind,matrix,ct);return matrix;
    }
    public static async Task<string> Export(OperationalSession s,OperationalMatrix matrix,CancellationToken ct=default)
    {
        var run=await Current(s,ct);if(run.Id!=matrix.RunId)throw new InvalidOperationException("La matriz pertenece a un procesamiento anterior.");
        var profile=Profile(s,matrix);var folder=Path.Combine(s.Root,"exports",s.Handle!.Id);
        string source;
        if(ManualCellReview.Events(s.Handle.Work,profile.Draft.Id).Length>0)
        {var receipt=await s.Coordinator.ExportManualPresentation(s.Handle,profile,matrix.Path,folder,s.Actor,DateTimeOffset.UtcNow,ct);source=Path.Combine(folder,receipt.FileName);}
        else
        {var receipt=await s.Coordinator.Export(s.Handle,profile.Draft.Id,matrix.Path,folder,s.Actor,DateTimeOffset.UtcNow,ct);source=Path.Combine(folder,receipt.FileName);}
        WorkingRowsExportReceipt? working=null;
        if(profile.Draft.Sheets.Any(sheet=>sheet.Name==SistemaMarcaciones.Monthly.ForwardAttendance.AttendanceWorkbook.Novelty))
        {working=await WorkingRowsExport.Export(s.Coordinator,s.Handle,profile,source,PhysicalMatrix.Hash(source),folder,s.Actor,DateTimeOffset.UtcNow,ct);source=Path.Combine(folder,working.FileName);}
        var compatible=ExcelPackageCompatibility.Create(CompletionExports.Pin(source),folder,"MONTHLY_"+matrix.SHA256[..12]);source=compatible.Workbook.Path;
        var parts=new Dictionary<string,byte[]>();using(var z=ZipFile.OpenRead(source))foreach(var e in z.Entries){using var f=e.Open();using var m=new MemoryStream();f.CopyTo(m);parts[e.FullName]=m.ToArray();}
        var wb=XDocument.Parse(Encoding.UTF8.GetString(parts["xl/workbook.xml"]));WorkbookViewCompatibility.EnsurePrimaryView(wb);parts["xl/workbook.xml"]=Xml(wb);
        var destination=Path.Combine(folder,"Matrices_"+s.PeriodFor("DIRECTORES").Start.ToString("yyyy_MM")+"_R"+s.Handle.Work.Revision+"_Excel.xlsx");WritePackage(destination,parts);
        await s.Event("CBA_OPERATIONAL_EXCEL_CHAIN_V1",new{matrix.Id,matrix.SHA256,Working=working,Compatibility=compatible,Final=CompletionExports.Pin(destination)},ct);
        await s.Event(OperationalSession.ExportEvent,new LocalExport(CanonicalMonthly.Hash(new{destination,Hash=PhysicalMatrix.Hash(destination)}),"EXCEL",destination,PhysicalMatrix.Hash(destination),s.Handle.Work.Revision),ct);return destination;
    }
    public static async Task<string[]> ExportAll(OperationalSession s,CancellationToken ct=default)
    {await Generate(s,ct);var files=new List<string>();foreach(var matrix in Matrices(s).Where(m=>m.RunId==s.LastRun!.Id))files.Add(await Export(s,matrix,ct));return files.ToArray();}
    public static Task WriteImportTemplates(string root)
    {
        var folder=Path.Combine(root,"formatos_de_carga");Directory.CreateDirectory(folder);
        foreach(var (name,text) in new[]{("Personal.csv","Cédula;Nombres y apellidos;Cargo;Población;Desde;Hasta;Código biométrico\r\n"),("Jornadas.csv","Cédula;Fecha;Inicio;Fin;Tipo;Pelotón;Referencia;Estado\r\n"),("Feriados.csv","Fecha;Descripción;Referencia\r\n")})
        {var path=Path.Combine(folder,name);if(!File.Exists(path))File.WriteAllText(path,text,new UTF8Encoding(true));}
        OperationalImportTemplates.Write(folder);
        return Task.CompletedTask;
    }
    static XElement Paragraph(string text,bool bold=false)=>new(W+"p",new XElement(W+"r",bold?new XElement(W+"rPr",new XElement(W+"b")):null,new XElement(W+"t",new XAttribute(XNamespace.Xml+"space","preserve"),text)));
    public static async Task<string[]> Memoranda(OperationalSession s,CancellationToken ct=default)
    {
        var run=await Current(s,ct);var files=new List<string>();
        foreach(var group in run.Days.Where(d=>d.State=="COMPLETED").SelectMany(d=>d.Cases).Where(c=>c.State is "PENDIENTE_JUSTIFICACION" or "PENDIENTE_REGULARIZACION_DOCUMENTAL").GroupBy(c=>c.PersonId))
        {
            ct.ThrowIfCancellationRequested();var person=s.People.Single(p=>p.Id==group.Key);
            var cut=s.PeriodFor(person.Population);
            var body=new XElement(W+"body",Paragraph("CUERPO DE BOMBEROS DE AMBATO",true),Paragraph("TALENTO HUMANO",true),Paragraph("BORRADOR PARA REVISIÓN · NO EMITIDO",true),
                Paragraph("PARA: "+person.Name+" · "+person.Position),Paragraph("ASUNTO: Requerimiento de revisión y justificación de novedades de marcación"),
                Paragraph($"PERÍODO: {cut.Start:dd/MM/yyyy} a {cut.End:dd/MM/yyyy}"),
                Paragraph("Se presenta para revisión el siguiente detalle. La existencia de una inconsistencia no constituye por sí sola una determinación de responsabilidad ni un descuento."));
            var table=new XElement(W+"tbl",new XElement(W+"tblPr",new XElement(W+"tblW",new XAttribute(W+"w",0),new XAttribute(W+"type","auto"))));
            void Row(params string[] values)=>table.Add(new XElement(W+"tr",values.Select(v=>new XElement(W+"tc",new XElement(W+"tcPr",new XElement(W+"tcW",new XAttribute(W+"w",2200),new XAttribute(W+"type","dxa"))),Paragraph(v)))));
            Row("Fecha","Marcaciones","Novedad y respaldo","Acción requerida");
            foreach(var c in group.OrderBy(c=>c.Date).ThenBy(c=>c.Id,StringComparer.Ordinal))Row(c.Date.ToString("dd/MM/yyyy"),string.Join(" · ",c.Marks),OperationalLabels.Case(c.Type)+". "+c.Description+". Documentos aplicados por el motor: "+(c.AppliedDocuments.Length==0?"ninguno dentro del alcance revisado":string.Join("; ",c.AppliedDocuments)),"Revisar y aportar el justificativo o solicitar la corrección con su evidencia.");
            body.Add(table,Paragraph("Documento consolidado por servidor. Pendiente de revisión de Talento Humano, adecuación al formato institucional y emisión por Quipux. Sin firma ni numeración oficial."),
                new XElement(W+"sectPr",new XElement(W+"pgSz",new XAttribute(W+"w",11906),new XAttribute(W+"h",16838)),new XElement(W+"pgMar",new XAttribute(W+"top",1134),new XAttribute(W+"right",1134),new XAttribute(W+"bottom",1134),new XAttribute(W+"left",1134))));
            XNamespace types="http://schemas.openxmlformats.org/package/2006/content-types";
            var parts=new Dictionary<string,byte[]>{["word/document.xml"]=Xml(new(new XElement(W+"document",new XAttribute(XNamespace.Xmlns+"w",W),body))),
                ["_rels/.rels"]=Xml(new(new XElement(P+"Relationships",new XElement(P+"Relationship",new XAttribute("Id","rId1"),new XAttribute("Type",R.NamespaceName+"/officeDocument"),new XAttribute("Target","word/document.xml"))))),
                ["[Content_Types].xml"]=Xml(new(new XElement(types+"Types",new XElement(types+"Default",new XAttribute("Extension","rels"),new XAttribute("ContentType","application/vnd.openxmlformats-package.relationships+xml")),new XElement(types+"Default",new XAttribute("Extension","xml"),new XAttribute("ContentType","application/xml")),new XElement(types+"Override",new XAttribute("PartName","/word/document.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")))))};
            var path=Path.Combine(s.Root,"exports",s.Handle!.Id,"memorandos",run.Id,"Borrador_"+person.Id+".docx");WritePackage(path,parts);
            var hash=PhysicalMatrix.Hash(path);if(!s.Exports.Any(e=>e.SHA256==hash&&e.Path==path))await s.Event(OperationalSession.ExportEvent,new LocalExport(hash,"WORD_DRAFT",path,hash,s.Handle.Work.Revision),ct);files.Add(path);
        }
        return files.ToArray();
    }
}
