using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using SistemaMarcaciones.Monthly.Controlled;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Monthly.Projects;
using SistemaMarcaciones.Workspace.StorageV2;

namespace SistemaMarcaciones.F13.Integrated.Native;
public sealed record WorkingRowsExportReceipt(string MatrixId,int Revision,string FileName,string SHA256,
    string OriginalReviewFile,string OriginalReviewSHA256,string ProjectionSHA256,int WorkingRows,string Status,string Sidecar);
public static class WorkingRowsExport
{
    public const string Kind="F13_WORKING_ECONOMIC_ROW_EXPORT_V1";
    public const string SheetName="Revisión de atrasos";
    static readonly XNamespace S=WorkbookProfiles.S;
    static readonly XNamespace R="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace Rel="http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace Ct="http://schemas.openxmlformats.org/package/2006/content-types";
    static readonly DateTimeOffset Stamp=new(2000,1,1,0,0,0,TimeSpan.Zero);
    static byte[] Xml(XDocument xml)=>Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
    static XElement Cell(int row,int col,string? value,int style=0,bool number=false)
    {
        var cell=new XElement(S+"c",new XAttribute("r",MatrixCoordinates.A1(row,col)),new XAttribute("s",style));
        if(value==null)return cell;
        if(number)cell.Add(new XElement(S+"v",value));
        else{cell.SetAttributeValue("t","inlineStr");cell.Add(new XElement(S+"is",new XElement(S+"t",new XAttribute(XNamespace.Xml+"space","preserve"),value)));}return cell;
    }
    public static XDocument Sheet(WorkingEconomicRows projection,MonthlyPeriod period,WorkbookProfile profile)
    {
        var reference=profile.Draft.Sheets.First(s=>projection.EconomicSheets.Contains(s.Name,StringComparer.Ordinal));
        int Style(MatrixCell? cell)=>int.TryParse(cell?.Style,out var id)?id:0;
        var headerStyle=Style(reference.Cells.FirstOrDefault(c=>c.Value=="NOMBRES"));
        var bodyStyle=Style(reference.Cells.FirstOrDefault(c=>projection.Rows.Any(r=>r.Name==c.Value)));
        var data=new XElement(S+"sheetData");
        data.Add(new XElement(S+"row",new XAttribute("r",1),Cell(1,1,"VALORES A DESCONTAR · REVISIÓN DE TRABAJO NO ACREDITADA",headerStyle)));
        data.Add(new XElement(S+"row",new XAttribute("r",2),new XAttribute("ht",36),new XAttribute("customHeight",1),Cell(2,1,$"{period.Start:dd/MM/yyyy} — {period.End:dd/MM/yyyy} · revisión {projection.Revision}. Sin cálculo económico nuevo."+(profile.Draft.Synthetic?" QA_SYNTHETIC_NOT_LABOR_RESULT. NO CERTIFICADO PARA USO INSTITUCIONAL.":""),bodyStyle)));
        string[] headers=["Servidor","Fechas con atrasos en revisión","Minutos en revisión","Valor original (referencia)","Estado económico"];
        data.Add(new XElement(S+"row",new XAttribute("r",3),new XAttribute("ht",36),new XAttribute("customHeight",1),headers.Select((h,i)=>Cell(3,i+1,h,headerStyle))));
        int at=4;
        foreach(var row in projection.Rows)
        {
            var amount=row.OriginalEconomicCells.LastOrDefault()?.Value;
            bool number=amount!=null&&decimal.TryParse(amount,NumberStyles.Number,CultureInfo.InvariantCulture,out _);
            data.Add(new XElement(S+"row",new XAttribute("r",at),new XAttribute("ht",Math.Max(36,18*Math.Ceiling(row.DateLabel.Length/40d))),new XAttribute("customHeight",1),Cell(at,1,row.Name,bodyStyle),Cell(at,2,row.DateLabel,bodyStyle),
                Cell(at,3,row.Minutes.ToString(CultureInfo.InvariantCulture),bodyStyle,number:true),Cell(at,4,amount,bodyStyle,number:number),Cell(at,5,row.EconomicState,bodyStyle)));at++;
        }
        data.Add(new XElement(S+"row",new XAttribute("r",at+1),new XAttribute("ht",40),new XAttribute("customHeight",1),Cell(at+1,1,WorkingEconomicRows.Warning,headerStyle)));
        data.Add(new XElement(S+"row",new XAttribute("r",at+2),new XAttribute("ht",36),new XAttribute("customHeight",1),Cell(at+2,1,$"{projection.ExcludedPeople} persona(s) sin atrasos conocidos en esta revisión; {projection.UnknownPersonDates} persona-fecha sin dato no se convierten en cero.",bodyStyle)));
        return new(new XElement(S+"worksheet",new XElement(S+"sheetViews",new XElement(S+"sheetView",new XAttribute("workbookViewId",0),
            new XElement(S+"pane",new XAttribute("ySplit",3),new XAttribute("topLeftCell","A4"),new XAttribute("activePane","bottomLeft"),new XAttribute("state","frozen")),
            new XElement(S+"selection",new XAttribute("pane","bottomLeft"),new XAttribute("activeCell","A4"),new XAttribute("sqref","A4")))),
            new XElement(S+"sheetFormatPr",new XAttribute("defaultRowHeight",24)),
            new XElement(S+"cols",new[]{22,48,22,28,55}.Select((w,i)=>new XElement(S+"col",new XAttribute("min",i+1),new XAttribute("max",i+1),new XAttribute("width",w),new XAttribute("customWidth",1)))),data,
            new XElement(S+"mergeCells",new XAttribute("count",4),new[]{"A1:E1","A2:E2",$"A{at+1}:E{at+1}",$"A{at+2}:E{at+2}"}.Select(m=>new XElement(S+"mergeCell",new XAttribute("ref",m))))));
    }
    public static async Task<WorkingRowsExportReceipt> Export(ProjectCoordinator coordinator,ProjectHandle h,WorkbookProfile profile,string originalReview,string expectedHash,string destination,string actor,DateTimeOffset at,CancellationToken ct=default)
    {
        destination=PathSafety.Sandbox(destination);HistoricalFinalMatrices.RequireWorking(profile.Draft);ProjectCoordinator.RequireWritable(h);
        await h.Serial.WaitAsync(ct);WorkingRowsExportReceipt receipt;
        try
        {
            var before=h.Work;var projection=WorkingEconomicRows.For(before,profile);
            // An older export with identical business data can lack its parent workbook view.
            // Keep its receipt intact and give this metadata-compatible export its own identity.
            var hash=CanonicalMonthly.Hash(new { ViewContract=ExcelPackageCompatibility.Contract,Projection=projection with{Revision=0} });
            var name=$"WORKING_ROWS_NOT_CERTIFIED_{profile.Draft.Id[..12]}_R{before.Revision}_{hash[..12]}.xlsx";
            var output=Path.Combine(destination,name);PathSafety.CheckLinks(output);
            if(PhysicalMatrix.Hash(originalReview)!=expectedHash)throw new InvalidDataException("ORIGINAL_REVIEW_EXPORT_CHANGED");
            var prior=ProjectEvents.Read<WorkingRowsExportReceipt>(before,Kind).SingleOrDefault(r=>r.ProjectionSHA256==hash&&r.OriginalReviewSHA256==expectedHash);
            if(prior!=null){if(PhysicalMatrix.Hash(Path.Combine(destination,prior.FileName))!=prior.SHA256)throw new InvalidDataException("SEALED_WORKING_EXPORT_CHANGED");return prior;}
            Directory.CreateDirectory(destination);var temp=Path.Combine(destination,"working-"+Guid.NewGuid().ToString("N")+".tmp");
            try
            {
                using(var source=ZipFile.OpenRead(originalReview))using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
                {
                    var workbook=WorkbookProfiles.Xml(source,"xl/workbook.xml");var relationships=WorkbookProfiles.Xml(source,"xl/_rels/workbook.xml.rels");var types=WorkbookProfiles.Xml(source,"[Content_Types].xml");
                    if(workbook.Descendants(S+"sheet").Any(s=>(string?)s.Attribute("name")==SheetName))throw new InvalidDataException("WORKING_SHEET_ALREADY_PRESENT_IN_SOURCE");
                    const string part="xl/worksheets/f13_working_rows.xml",id="rIdF13WorkingRowsV1";
                    if(source.GetEntry(part)!=null||relationships.Root!.Elements().Any(e=>(string?)e.Attribute("Id")==id))throw new InvalidDataException("WORKING_PART_COLLISION");
                    WorkbookViewCompatibility.EnsurePrimaryView(workbook);
                    var sheetId=workbook.Descendants(S+"sheet").Max(s=>(int)s.Attribute("sheetId")!)+1;
                    workbook.Root!.Element(S+"sheets")!.AddFirst(new XElement(S+"sheet",new XAttribute("name",SheetName),new XAttribute("sheetId",sheetId),new XAttribute(R+"id",id)));
                    // localSheetId is a workbook position: retain ownership after the new first working sheet.
                    foreach(var localName in workbook.Descendants(S+"definedName").Where(n=>n.Attribute("localSheetId")!=null))
                        localName.SetAttributeValue("localSheetId",(int)localName.Attribute("localSheetId")!+1);
                    foreach(var view in workbook.Descendants(S+"workbookView"))view.SetAttributeValue("activeTab",0);
                    relationships.Root.Add(new XElement(Rel+"Relationship",new XAttribute("Id",id),new XAttribute("Type",R.NamespaceName+"/worksheet"),new XAttribute("Target","worksheets/f13_working_rows.xml")));
                    types.Root!.Add(new XElement(Ct+"Override",new XAttribute("PartName","/"+part),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
                    var replacements=new Dictionary<string,byte[]>(StringComparer.Ordinal){["xl/workbook.xml"]=Xml(workbook),["xl/_rels/workbook.xml.rels"]=Xml(relationships),["[Content_Types].xml"]=Xml(types),[part]=Xml(Sheet(projection,profile.Draft.Period,profile))};
                    foreach(var existing in source.Entries.Where(e=>e.FullName.StartsWith("xl/worksheets/",StringComparison.Ordinal)&&e.FullName.EndsWith(".xml",StringComparison.Ordinal)))
                    {
                        var sheet=WorkbookProfiles.Xml(source,existing.FullName);
                        if(ExcelPackageCompatibility.ClampTrailingColumnMetadata(sheet))replacements[existing.FullName]=Xml(sheet);
                    }
                    using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
                    {
                        foreach(var entry in source.Entries.OrderBy(e=>e.FullName,StringComparer.Ordinal))
                        {
                            var next=zip.CreateEntry(entry.FullName,CompressionLevel.Optimal);next.LastWriteTime=Stamp;using var target=next.Open();
                            if(replacements.Remove(entry.FullName,out var bytes))target.Write(bytes);else{using var input=entry.Open();input.CopyTo(target);}
                        }
                        foreach(var pair in replacements){var entry=zip.CreateEntry(pair.Key,CompressionLevel.Optimal);entry.LastWriteTime=Stamp;using var target=entry.Open();target.Write(pair.Value);}
                    }
                    stream.Flush(true);
                }
                ct.ThrowIfCancellationRequested();
                if(File.Exists(output)){if(PhysicalMatrix.Hash(output)!=PhysicalMatrix.Hash(temp))throw new InvalidDataException("SEALED_WORKING_OUTPUT_COLLISION");File.Delete(temp);}else File.Move(temp,output,false);
            }finally{if(File.Exists(temp))File.Delete(temp);}
            var sidecar=output+".json";
            NativeAttendanceCoordinator.WriteExact(sidecar,new{WorkingEconomicRows.Contract,Projection=projection,OriginalReviewFile=Path.GetFileName(originalReview),OriginalReviewSHA256=expectedHash,
                Status="MANUAL_WORKING_ROWS_NOT_LABOUR_OR_ECONOMIC_CERTIFICATION",EconomicRecalculated=false,
                OriginalWorksheetCellsFormulasCachesStylesMergesPreserved=true,TrailingColumnMetadataLimitedToExcelGrid=true,UnknownsNotZero=true});
            receipt=new(profile.Draft.Id,before.Revision,name,PhysicalMatrix.Hash(output),Path.GetFileName(originalReview),expectedHash,hash,projection.Rows.Length,"MANUAL_WORKING_ROWS_NOT_CERTIFIED",sidecar);
            await h.Repository.Save(ProjectEvents.Append(before,Kind,receipt,actor,at),before.Revision,ct);
        }finally{h.Serial.Release();}
        await coordinator.Refresh(h,CancellationToken.None);return receipt;
    }
}
