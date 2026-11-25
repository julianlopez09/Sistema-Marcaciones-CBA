using System.Globalization;
using System.Text;
using System.Xml.Linq;
using SistemaMarcaciones.F13.Integrated.Native;
using SistemaMarcaciones.Monthly.Controlled;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Monthly.Export.DirectoresGuardia.Dynamic;
using SistemaMarcaciones.Monthly.ForwardAttendance;
using SistemaMarcaciones.Monthly.Projects;

namespace SistemaMarcaciones.F13.Operational;

/// <summary>New local transport over the exact accepted physical layouts. Original layouts and kernels are read-only.</summary>
public static class OperationalPhysical
{
    public const string Contract="CBA_LOCAL_CAPTURED_RESULTS_PHYSICAL_TRANSPORT_V1";
    static readonly XNamespace S="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static byte[] Bytes(XDocument d)=>Encoding.UTF8.GetBytes(d.ToString(SaveOptions.DisableFormatting));
    static XDocument Read(byte[] bytes)=>XDocument.Parse(Encoding.UTF8.GetString(bytes));
    static void Text(XElement c,string? value,bool numeric=false)
    {
        c.Elements(S+"f").Remove();c.Elements(S+"v").Remove();c.Elements(S+"is").Remove();c.Attribute("t")?.Remove();
        if(value==null)return;if(numeric)c.Add(new XElement(S+"v",value));
        else{c.SetAttributeValue("t","inlineStr");c.Add(new XElement(S+"is",new XElement(S+"t",new XAttribute(XNamespace.Xml+"space","preserve"),value)));}
    }
    static string? Category(string? value)=>value switch{"PERMISO PERSONAL"=>"permiso_personal","PERMISO MÉDICO"=>"permiso_medico","LICENCIA"=>"licencia","VACACIONES"=>"vacaciones",_=>value?.StartsWith("NO REGISTRA",StringComparison.Ordinal)==true?"no_registra":decimal.TryParse(value,NumberStyles.Number,CultureInfo.InvariantCulture,out var n)&&n>0?"atraso":null};
    static string? Color(string? value)=>value switch{"PERMISO PERSONAL"=>"92D050","PERMISO MÉDICO"=>"DDEBF7","LICENCIA"=>"CC66CC","VACACIONES"=>"F4B183",_=>value?.StartsWith("NO REGISTRA",StringComparison.Ordinal)==true?"FF0000":value=="MARCACIONES ADICIONALES"?"FCE8E8":decimal.TryParse(value,NumberStyles.Number,CultureInfo.InvariantCulture,out var n)&&n>0?"FF0000":null};
    static XElement StyleCell(XElement cell,XDocument styles,string? color)
    {
        if(color==null)return cell;
        var fills=styles.Root!.Element(S+"fills")!;var xfs=styles.Root.Element(S+"cellXfs")!;
        var fill=fills.Elements().Select((e,i)=>(e,i)).FirstOrDefault(x=>(string?)x.e.Element(S+"patternFill")?.Element(S+"fgColor")?.Attribute("rgb")=="FF"+color);
        int id=fill.e==null?fills.Elements().Count():fill.i;
        if(fill.e==null)fills.Add(new XElement(S+"fill",new XElement(S+"patternFill",new XAttribute("patternType","solid"),new XElement(S+"fgColor",new XAttribute("rgb","FF"+color)),new XElement(S+"bgColor",new XAttribute("indexed",64)))));
        var original=int.Parse((string?)cell.Attribute("s")??"0",CultureInfo.InvariantCulture);
        var xf=new XElement(xfs.Elements().ElementAt(original));xf.SetAttributeValue("fillId",id);xf.SetAttributeValue("applyFill",1);
        var index=xfs.Elements().Count();xfs.Add(xf);cell.SetAttributeValue("s",index);
        fills.SetAttributeValue("count",fills.Elements().Count());xfs.SetAttributeValue("count",xfs.Elements().Count());return cell;
    }
    static void FixViews(Dictionary<string,byte[]> parts)
    {
        var book=Read(parts["xl/workbook.xml"]);WorkbookViewCompatibility.EnsurePrimaryView(book);
        var calc=book.Root!.Element(S+"calcPr");if(calc==null){calc=new XElement(S+"calcPr");book.Root.Add(calc);}calc.SetAttributeValue("calcMode","manual");calc.SetAttributeValue("fullCalcOnLoad",0);calc.SetAttributeValue("forceFullCalc",0);
        parts["xl/workbook.xml"]=Bytes(book);
        foreach(var name in parts.Keys.Where(k=>k.StartsWith("xl/worksheets/",StringComparison.Ordinal)&&k.EndsWith(".xml",StringComparison.Ordinal)).ToArray())
        {var sheet=Read(parts[name]);if(ExcelPackageCompatibility.ClampTrailingColumnMetadata(sheet))parts[name]=Bytes(sheet);}
    }
    static (string Path,string Hash) Template(OperationalSession s,string population)
    {
        var hash=population switch{"ADMIN_OPADMIN"=>AttendanceWorkbook.AcceptedAnonymousStencilSHA,"DIRECTORES"=>"21506E7E0D4FF2EEF20EF73285FDED38695397EB4656C428C70D9DEF37FCE8A4",_=>"C97C196F0DD04286B9CD618CFC456E7D0E6977EEA2EDF518702F8EC172A2BAC5"};
        var file=Path.Combine(s.TemplateRoot,population+".xlsx");if(PhysicalMatrix.Hash(file)!=hash)throw new InvalidDataException("Cambió la plantilla admitida de "+population+".");return(file,hash);
    }
    public static async Task<OperationalMatrix[]> Generate(OperationalSession s,CancellationToken ct=default)
    {
        var run=s.LastRun??throw new InvalidOperationException("Procesa el mes antes de generar matrices.");
        if(!s.MatchesProfile(run))throw new InvalidOperationException("El resultado pertenece a otro perfil de revisión.");
        if(run.SourceSetSHA256!=await s.SourceSet(ct)||run.ReviewSHA256!=CanonicalMonthly.Hash(s.Review))throw new InvalidOperationException("El procesamiento está desactualizado.");
        var facts=await OperationalFacts.Capture(s,ct);var result=new List<OperationalMatrix>();
        foreach(var group in new[]{"ADMIN_OPADMIN","DIRECTORES","GUARDIA"})
        {
            var people=s.People.Where(p=>group=="ADMIN_OPADMIN"?p.Population is "ADMIN" or "OPADMIN":p.Population==group).OrderBy(p=>p.Id,StringComparer.Ordinal).ToArray();
            if(people.Length==0)continue;
            var id=Contract+":"+run.Id+":"+group;var prior=OperationalArtifacts.Matrices(s).SingleOrDefault(m=>m.Id==id);
            if(prior!=null){OperationalArtifacts.Profile(s,prior);result.Add(prior);continue;}
            var cut=s.PeriodFor(group=="ADMIN_OPADMIN"?"ADMIN":group);var dates=Enumerable.Range(0,cut.End.DayNumber-cut.Start.DayNumber+1).Select(i=>cut.Start.AddDays(i)).ToArray();
            var template=Template(s,group);Dictionary<string,byte[]> parts;var bindings=new Dictionary<string,CellBinding>(StringComparer.Ordinal);
            var stylesByCell=new Dictionary<string,string?>();
            var valuesByCell=new Dictionary<string,string?>();
            if(group=="ADMIN_OPADMIN")
            {
                var cellRows=people.SelectMany(p=>dates.Select(date=>
                {
                    var day=run.Days.SingleOrDefault(d=>d.PersonId==p.Id&&d.Date==date);var fs=facts.Where(f=>f.PersonId==p.Id&&f.Date==date).ToArray();
                    return new AttendanceDateCell(p.Id,date,Enum.Parse<AttendancePopulation>(p.Population),fs,day?.State??"PENDING_EVIDENCE",day?.State=="COMPLETED"?day.Value:"EVIDENCIA INSUFICIENTE",day?.State=="COMPLETED"?Category(day.Value):null);
                })).ToArray();
                var projection=new AttendanceProjection(s.Handle!.Id,cut,run.Id,run.SourceSetSHA256,CanonicalMonthly.Hash(facts),people.Select(p=>new AttendancePerson(p.Id,Enum.Parse<AttendancePopulation>(p.Population),OperationalFacts.Classification(p.Population),null,p.Position,OperationalFacts.Metadata(p))).ToArray(),dates,cellRows,false);
                var receipt=AttendanceWorkbook.Render(template.Path,template.Hash,Path.Combine(s.Root,"layout_transport",run.Id),projection);
                parts=DynamicPhysicalRenderer.ReadParts(receipt.Asset.FileName);
                foreach(var loc in receipt.Locators)
                {
                    var day=run.Days.SingleOrDefault(d=>d.PersonId==loc.PersonId&&d.Date==loc.Date);var docIds=OperationalFacts.Applied(day);
                    bindings[MatrixReview.Key(loc.Sheet,loc.Coordinate)]=new(loc.PersonId,loc.Date,day?.Cases.Length==1?day.Cases[0].Id:null,docIds.Length==1?docIds[0]:null,Contract+":"+run.Id);
                    stylesByCell[loc.Coordinate]=day?.State=="COMPLETED"?Color(day.Value):null;
                    valuesByCell[loc.Coordinate]=day?.State=="COMPLETED"?day.Value:"EVIDENCIA INSUFICIENTE";
                }
                var styles=Read(parts["xl/styles.xml"]);
                foreach(var part in parts.Keys.Where(k=>k.StartsWith("xl/worksheets/sheet",StringComparison.Ordinal)&&k.EndsWith(".xml",StringComparison.Ordinal)).ToArray())
                {
                    var sheet=Read(parts[part]);foreach(var c in sheet.Descendants(S+"c"))
                    {
                        var text=string.Concat(c.Descendants(S+"t").Select(t=>t.Value));var person=people.SingleOrDefault(p=>p.Id==text);if(person!=null)Text(c,person.Name);
                        if(part=="xl/worksheets/sheet1.xml"&&stylesByCell.TryGetValue(c.Attribute("r")!.Value,out var color))
                        {var value=valuesByCell[c.Attribute("r")!.Value];Text(c,value,decimal.TryParse(value,NumberStyles.Number,CultureInfo.InvariantCulture,out _));StyleCell(c,styles,color);}
                    }
                    parts[part]=Bytes(sheet);
                }
                parts["xl/styles.xml"]=Bytes(styles);
            }
            else
            {
                // The existing renderer supplies an empty, characterized layout only. It is not used to assert authority or fictitious results.
                var plan=DynamicPhysicalRenderer.Plan(template.Path,template.Hash,new(new(cut.Start,cut.End),dates,[],[],group,DynamicMonthlyExportCatalog.Version,DynamicMode.F13_VERIFIED_RESULT_PREVIEW,s.Root));
                parts=plan.Parts;var sheet=Read(parts["xl/worksheets/sheet1.xml"]);var templateParts=DynamicPhysicalRenderer.ReadParts(template.Path);var source=Read(templateParts["xl/worksheets/sheet1.xml"]);
                var prototype=source.Root!.Element(S+"sheetData")!.Elements(S+"row").Single(r=>(string?)r.Attribute("r")=="5");
                var style=Read(parts["xl/styles.xml"]);var data=sheet.Root!.Element(S+"sheetData")!;int first=group=="DIRECTORES"?5:6;
                for(var i=0;i<people.Length;i++)
                {
                    var row=i+5;var p=people[i];var r=new XElement(S+"row",prototype.Attributes().Where(a=>a.Name.LocalName is not ("r" or "spans")),new XAttribute("r",row));
                    string?[] identity=first==5?[(i+1).ToString(),p.Name,OperationalFacts.Classification(p.Population),"DIRECTOR"]:[(i+1).ToString(),p.Name,s.Duties.FirstOrDefault(d=>d.PersonId==p.Id)?.Platoon,OperationalFacts.Classification(p.Population),"GUARDIA"];
                    void Add(int column,string? value,string? color=null)
                    {
                        var prototypeCell=prototype.Elements(S+"c").FirstOrDefault(c=>MatrixCoordinates.Parse((string)c.Attribute("r")!).Column==Math.Min(column,first));
                        var c=prototypeCell==null?new XElement(S+"c"):new XElement(prototypeCell);c.SetAttributeValue("r",MatrixCoordinates.A1(row,column));Text(c,value,decimal.TryParse(value,NumberStyles.Number,CultureInfo.InvariantCulture,out _));StyleCell(c,style,color);r.Add(c);
                    }
                    for(var j=0;j<identity.Length;j++)Add(j+1,identity[j]);
                    for(var j=0;j<dates.Length;j++)
                    {
                        var day=run.Days.SingleOrDefault(d=>d.PersonId==p.Id&&d.Date==dates[j]);var value=day?.State=="COMPLETED"?day.Value:"EVIDENCIA INSUFICIENTE";Add(first+j,value,Color(value));
                        var docs=OperationalFacts.Applied(day);
                        bindings[MatrixReview.Key(group=="DIRECTORES"?"REPORTE":"FORMATO MATRIZ OPERATIVOS",MatrixCoordinates.A1(row,first+j))]=new(p.Id,dates[j],day?.Cases.Length==1?day.Cases[0].Id:null,docs.Length==1?docs[0]:null,Contract+":"+run.Id);
                    }
                    Add(first+dates.Length+(first==5?2:0),"COBERTURA PARCIAL; SIN NUEVO CÁLCULO");data.Add(r);
                }
                sheet.Root.Element(S+"dimension")!.SetAttributeValue("ref","A1:"+MatrixCoordinates.A1(Math.Max(4,people.Length+4),first+dates.Length+(first==5?2:0)));
                var label=data.Elements(S+"row").Single(r=>(string?)r.Attribute("r")=="2").Element(S+"c")!;Text(label,$"{cut.Start:dd/MM/yyyy} a {cut.End:dd/MM/yyyy} · {people.Length} servidores · revisión local no certificada");
                parts["xl/worksheets/sheet1.xml"]=Bytes(sheet);parts["xl/styles.xml"]=Bytes(style);
            }
            FixViews(parts);var output=Path.Combine(s.Root,"matrices",s.Handle!.Id,run.Id,group+"_revision_local.xlsx");
            ct.ThrowIfCancellationRequested();OperationalArtifacts.WritePackage(output,parts);
            var m=new OperationalMatrix(id,output,PhysicalMatrix.Hash(output),run.Id,run.SourceSetSHA256,bindings);
            await s.Coordinator.Add(s.Handle,OperationalArtifacts.Profile(s,m),s.Actor,DateTimeOffset.UtcNow,ct);await s.Event(OperationalArtifacts.MatrixKind,m,ct);result.Add(m);
        }
        return result.ToArray();
    }
}
