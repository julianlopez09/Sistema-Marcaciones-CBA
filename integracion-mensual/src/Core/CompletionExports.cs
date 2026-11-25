using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SistemaMarcaciones.Monthly.Controlled;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Monthly.Host.NativeIntegration;
using SistemaMarcaciones.Monthly.Host.Wiring;
using SistemaMarcaciones.Monthly.Native.Outcomes;
using SistemaMarcaciones.Monthly.Projects;
using SistemaMarcaciones.Presentation.Contracts;
using SistemaMarcaciones.Workspace.StorageV2;

namespace SistemaMarcaciones.F13.Integrated.Native;

public sealed record CompletionExportReceipt(string MatrixId,string StateSHA256,int SourceRevision,int ConfirmedRevision,
    string Destination,B09File Attendance,B09File Review,B09File Manifest,int WorkingRows);
public sealed record TypedExportCell(string Sheet,string Coordinate,string PersonId,DateOnly Date,decimal Minutes,string Fill);
public sealed record TypedExportReceipt(B09File Workbook,B09File Manifest,TypedExportCell[] Cells);

/// <summary>One additive export boundary for Host and detached editor. Existing exporters and V2 CAS remain authoritative.</summary>
public static class CompletionExports
{
    public const string Kind="F13_ADMIN_OPADMIN_COMPLETION_EXPORT_V1";
    public static B09File Pin(string path)=>new(Path.GetFullPath(path),PhysicalMatrix.Hash(path));
    public static void Verify(B09File pin)
    { if(PhysicalMatrix.Hash(pin.Path)!=pin.SHA256)throw new InvalidDataException("COMPLETION_EXPORT_FILE_CHANGED"); }
    public static NativeMatrixRegistration Registration(ProjectHandle h,WorkbookProfile profile,string source)
    {
        var registration=ProjectEvents.Read<NativeMatrixRegistration>(h.Work,NativeAttendanceCoordinator.Contract).Single(r=>r.MatrixId==profile.Draft.Id);
        NativeAttendanceCoordinator.Verify(registration);
        if(registration.ProjectId!=h.Id||registration.Workbook.Asset.SourcePeriod!=profile.Draft.Period
            ||registration.Workbook.Asset.SHA256!=profile.Draft.SourceSHA256||PhysicalMatrix.Hash(source)!=profile.Draft.SourceSHA256)
            throw new InvalidDataException("COMPLETION_EXPORT_EXACT_NATIVE_MATRIX_REQUIRED");
        return registration;
    }
    public static string StateHash(MonthlyWork work,WorkbookProfile profile,NativeMatrixRegistration registration)=>CanonicalMonthly.Hash(new{
        Contract=Kind,ExportMetadataContract=ExcelPackageCompatibility.Contract,registration.SourceSetSHA256,registration.Id,Definition=profile.Draft,
        Reviewed=MatrixReview.ReviewedHash(work,profile.Draft),Manual=ManualCellReview.Current(work,profile),
        Ranges=ManualRangeReview.Active(work,profile.Draft.Id),Working=WorkingEconomicRows.For(work,profile) with{Revision=0},
        NativeEvents=work.Events.Where(e=>e.Kind is MonthlyHostCommands.NativeExecutionKind or B09Session.DocumentaryKind or B07Contract.Event).ToArray()
    });
    // Exact prior business-state identity, solely for carrying byte-verified frozen
    // manual export artifacts into a new destination after the SR3 metadata change.
    // It never promotes the old working-row workbook as a compatible new export.
    static string LegacyStateHash(MonthlyWork work,WorkbookProfile profile,NativeMatrixRegistration registration)=>CanonicalMonthly.Hash(new{
        Contract=Kind,registration.SourceSetSHA256,registration.Id,Definition=profile.Draft,
        Reviewed=MatrixReview.ReviewedHash(work,profile.Draft),Manual=ManualCellReview.Current(work,profile),
        Ranges=ManualRangeReview.Active(work,profile.Draft.Id),Working=WorkingEconomicRows.For(work,profile) with{Revision=0},
        NativeEvents=work.Events.Where(e=>e.Kind is MonthlyHostCommands.NativeExecutionKind or B09Session.DocumentaryKind or B07Contract.Event).ToArray()
    });
    static void CheckState(ProjectHandle h,WorkbookProfile profile,NativeMatrixRegistration registration,string state)
    { if(StateHash(h.Work,profile,registration)!=state)throw new InvalidOperationException("CAS_COMPLETION_EXPORT_SOURCE_CHANGED"); }
    static void CarryCompletionArtifactPorts(MonthlyWork work,string state,string legacyState,string destination)
    {
        var receipts=ProjectEvents.Read<CompletionExportReceipt>(work,Kind);
        var prior=receipts.LastOrDefault(r=>r.StateSHA256==state)??receipts.LastOrDefault(r=>r.StateSHA256==legacyState);
        if(prior==null)return;
        Verify(prior);
        using var manifest=JsonDocument.Parse(File.ReadAllText(prior.Manifest.Path));
        foreach(var pin in manifest.RootElement.GetProperty("Files").Deserialize<B09File[]>(B07Contract.Wire)!
            .Where(p=>(p.Path.EndsWith(".xlsx",StringComparison.OrdinalIgnoreCase)||p.Path.EndsWith(".xlsx.json",StringComparison.OrdinalIgnoreCase))
                &&!Path.GetFileName(p.Path).StartsWith("TYPED_COLOR_REVIEW_NOT_CERTIFIED_",StringComparison.Ordinal)
                &&!Path.GetFileName(p.Path).StartsWith("WORKING_ROWS_NOT_CERTIFIED_",StringComparison.Ordinal)))
        {
            var target=Path.Combine(destination,Path.GetFileName(pin.Path));PathSafety.CheckLinks(target);Verify(pin);
            if(File.Exists(target)){Verify(new B09File(target,pin.SHA256));continue;}
            Directory.CreateDirectory(destination);File.Copy(pin.Path,target,false);Verify(new B09File(target,pin.SHA256));
        }
    }
    static void CarryFrozenExportPort(MonthlyWork work,WorkbookProfile profile,NativeMatrixRegistration registration,string destination)
    {
        // The frozen export port keys idempotence by matrix/state, not destination. Carry its exact
        // verified artifact to a new QA destination; never rewrite that contract or infer results from it.
        var hash=MatrixReview.ReviewedHash(work,profile.Draft);
        var prior=ProjectEvents.Read<ReviewedExport>(work,MatrixReview.ExportKind).LastOrDefault(r=>r.MatrixId==profile.Draft.Id&&r.DraftHash==hash);
        if(prior==null)return;
        var target=Path.Combine(destination,prior.FileName);PathSafety.CheckLinks(target);
        if(File.Exists(target)){if(PhysicalMatrix.Hash(target)!=prior.SHA256)throw new InvalidDataException("FROZEN_EXPORT_DESTINATION_CHANGED");return;}
        var completed=ProjectEvents.Read<CompletionExportReceipt>(work,Kind).LastOrDefault(r=>r.Attendance.SHA256==prior.SHA256);
        B09File source;
        if(completed!=null){Verify(completed);source=completed.Attendance;}
        else
        {
            var link=ProjectEvents.Read<NativeExportLink>(work,NativeAttendanceCoordinator.ExportKind).LastOrDefault(r=>r.MatrixId==profile.Draft.Id&&r.Export.SHA256==prior.SHA256&&r.SourceSetSHA256==registration.SourceSetSHA256)
                ??throw new InvalidDataException("EXACT_FROZEN_EXPORT_SOURCE_WITNESS_REQUIRED");
            Verify(new B09File(link.EvidencePath,link.EvidenceSHA256));source=new(Path.Combine(Path.GetDirectoryName(link.EvidencePath)!,prior.FileName),prior.SHA256);
        }
        Verify(source);Directory.CreateDirectory(destination);File.Copy(source.Path,target,false);Verify(new B09File(target,prior.SHA256));
    }
    public static void Verify(CompletionExportReceipt receipt)
    {
        Verify(receipt.Attendance);Verify(receipt.Review);Verify(receipt.Manifest);
        using var document=JsonDocument.Parse(File.ReadAllText(receipt.Manifest.Path));
        var root=document.RootElement;
        if(root.GetProperty("Contract").GetString()!=Kind||root.GetProperty("StateSHA256").GetString()!=receipt.StateSHA256
            ||root.GetProperty("SourceRevision").GetInt32()!=receipt.SourceRevision)throw new InvalidDataException("COMPLETION_EXPORT_MANIFEST_CHANGED");
        foreach(var file in root.GetProperty("Files").Deserialize<B09File[]>(B07Contract.Wire)!)Verify(file);
    }
    public static async Task<CompletionExportReceipt> Export(ProjectCoordinator coordinator,ProjectHandle h,WorkbookProfile profile,
        string source,string destination,int expectedRevision,string actor,DateTimeOffset at,CancellationToken ct=default)
    {
        destination=PathSafety.Sandbox(destination);PathSafety.CheckLinks(destination);HistoricalFinalMatrices.RequireWorking(profile.Draft);
        await coordinator.Refresh(h,ct);ProjectCoordinator.RequireWritable(h);
        if(h.Work.Revision!=expectedRevision)throw new InvalidOperationException("CAS_COMPLETION_EXPORT_REVISION_STALE");
        var registration=Registration(h,profile,source);var before=h.Work;var state=StateHash(before,profile,registration);
        var prior=ProjectEvents.Read<CompletionExportReceipt>(before,Kind).LastOrDefault(r=>r.MatrixId==profile.Draft.Id&&r.StateSHA256==state&&r.Destination==destination);
        if(prior!=null){Verify(prior);return prior;}
        CarryCompletionArtifactPorts(before,state,LegacyStateHash(before,profile,registration),destination);
        CarryFrozenExportPort(before,profile,registration,destination);
        var original=await coordinator.Export(h,profile.Draft.Id,source,destination,actor,at,ct);
        CheckState(h,profile,registration,state);
        var attendance=Pin(Path.Combine(destination,original.FileName));
        var files=new List<B09File>{attendance,Pin(registration.Workbook.SidecarPath),Pin(registration.NativeSidecar)};
        B09File review=attendance;
        if(ManualCellReview.Current(h.Work,profile).Count>0)
        {
            var manual=await coordinator.ExportManualPresentation(h,profile,source,destination,actor,at,ct);
            CheckState(h,profile,registration,state);
            var manualFile=Pin(Path.Combine(destination,manual.FileName));files.Add(manualFile);files.Add(Pin(Path.Combine(destination,manual.ManifestFile)));
            var typed=TypedColors(h.Work,profile,manualFile,destination);review=typed.Workbook;
            files.Add(typed.Workbook);files.Add(typed.Manifest);
        }
        CheckState(h,profile,registration,state);
        var working=await WorkingRowsExport.Export(coordinator,h,profile,review.Path,review.SHA256,destination,actor,at,ct);
        CheckState(h,profile,registration,state);review=Pin(Path.Combine(destination,working.FileName));files.Add(review);files.Add(Pin(working.Sidecar));
        // Preserve the frozen original export and expose a separately identified,
        // Excel-compatible presentation copy as the user-facing attendance artifact.
        var compatibleAttendance=ExcelPackageCompatibility.Create(attendance,destination,"ADMIN_OPADMIN_ORIGINAL");
        files.Add(compatibleAttendance.Workbook);files.Add(compatibleAttendance.Manifest);attendance=compatibleAttendance.Workbook;
        using var native=JsonDocument.Parse(File.ReadAllText(registration.NativeSidecar));
        var bundle=native.RootElement.GetProperty("Bundle").Deserialize<NativeAttendanceBundle>(B07Contract.Wire)!;
        var manifestPath=Path.Combine(destination,$"COMPLETION_{profile.Draft.Id[..12]}_{state[..12]}.json");
        NativeAttendanceCoordinator.WriteExact(manifestPath,new{
            Contract=Kind,StateSHA256=state,SourceRevision=before.Revision,registration.MatrixId,NativeRegistrationRevision=registration.SourceRevision,
            NativeSourceSHA256=registration.SourceSetSHA256,Bundle=bundle,OriginalRegistration=registration,OriginalExport=original,
            Manual=ManualCellReview.Current(before,profile),Pending=CrossMatrixReview.For(before).Matrix(profile.Draft.Id),
            Working=WorkingEconomicRows.For(before,profile),Files=files.DistinctBy(f=>f.Path).ToArray(),
            SourceAuthority="DURABLE_NATIVE_CSHARP_RECEIPTS_NOT_AUXILIARY_XLSX",EngineTraceContract="ENGINE_TRACE_COMPLETE",
            Status="QA_REVIEW_NOT_LABOUR_OR_ECONOMIC_CERTIFICATION",ProposedAmount=(decimal?)null,
            LabourReevaluated=false,EconomicRecalculated=false,UnknownsNotZero=true,NoNewDiscountFormula=true
        });
        await h.Serial.WaitAsync(ct);CompletionExportReceipt receipt;
        try
        {
            await h.Service.Reload(ct);ProjectCoordinator.RequireWritable(h);CheckState(h,profile,registration,state);
            var current=h.Work;
            receipt=new(profile.Draft.Id,state,before.Revision,current.Revision+1,destination,attendance,review,Pin(manifestPath),working.WorkingRows);
            await h.Repository.Save(ProjectEvents.Append(current,Kind,receipt,actor,at),current.Revision,ct);
            await h.Service.Reload(CancellationToken.None);
            var confirmed=ProjectEvents.Read<CompletionExportReceipt>(h.Work,Kind).Single(r=>r.Manifest==receipt.Manifest);
            if(h.Work.Revision!=confirmed.ConfirmedRevision)throw new InvalidDataException("COMPLETION_EXPORT_DURABLE_REVISION_NOT_CONFIRMED");
            Verify(confirmed);
        } finally {h.Serial.Release();}
        await coordinator.Refresh(h,CancellationToken.None);return receipt;
    }

    /// <summary>Presentation transport only. No frozen classifier changes, no numeric-text inference or formula edits.</summary>
    public static TypedExportReceipt TypedColors(MonthlyWork work,WorkbookProfile profile,B09File source,string destination)
    {
        destination=PathSafety.Sandbox(destination);Verify(source);HistoricalFinalMatrices.RequireWorking(profile.Draft);
        var rows=WorkingEconomicRows.For(work,profile);
        var cells=rows.Rows.SelectMany(r=>r.Dates).Where(d=>d.Origin==TypedLateReview.Kind)
            .Select(d=>new TypedExportCell(d.Sheet,d.Coordinate,d.PersonKey,d.Date,d.Minutes,SharedNoveltyCatalog.Color("atraso")!))
            .OrderBy(c=>c.Sheet,StringComparer.Ordinal).ThenBy(c=>c.Coordinate,StringComparer.Ordinal).ThenBy(c=>c.Date).ToArray();
        var key=CanonicalMonthly.Hash(new{source.SHA256,Cells=cells,Catalog=SharedNoveltyCatalog.Fingerprint});
        var output=Path.Combine(destination,"TYPED_COLOR_REVIEW_NOT_CERTIFIED_"+key[..16]+".xlsx");
        HistoricalFinalMatrices.RequireNewDestination(output);PathSafety.CheckLinks(output);Directory.CreateDirectory(destination);
        var s=WorkbookProfiles.S;
        using(var input=ZipFile.OpenRead(source.Path))using(var memory=new MemoryStream())
        {
            var parts=WorkbookProfiles.SheetParts(input);var changed=new Dictionary<string,XDocument>(StringComparer.Ordinal);
            if(cells.Length>0)
            {
                var styles=WorkbookProfiles.Xml(input,"xl/styles.xml");var fills=styles.Root!.Element(s+"fills")!;var xfs=styles.Root.Element(s+"cellXfs")!;
                var map=new Dictionary<(string,string),int>();
                foreach(var group in cells.GroupBy(c=>c.Sheet,StringComparer.Ordinal))
                {
                    var sheet=WorkbookProfiles.Xml(input,parts[group.Key]);
                    foreach(var cell in group.DistinctBy(c=>c.Coordinate))
                    {
                        var node=sheet.Descendants(s+"c").Single(c=>(string?)c.Attribute("r")==cell.Coordinate);
                        if(node.Element(s+"f")!=null)throw new InvalidDataException("TYPED_COLOR_FORMULA_IMMUTABLE");
                        var basis=(string?)node.Attribute("s")??"0";var pair=(basis,cell.Fill);
                        if(!map.TryGetValue(pair,out var style))
                        {
                            var fill=fills.Elements().Count();fills.Add(new XElement(s+"fill",new XElement(s+"patternFill",new XAttribute("patternType","solid"),new XElement(s+"fgColor",new XAttribute("rgb","FF"+cell.Fill)),new XElement(s+"bgColor",new XAttribute("indexed",64)))));
                            var xf=new XElement(xfs.Elements().ElementAt(int.Parse(basis,System.Globalization.CultureInfo.InvariantCulture)));xf.SetAttributeValue("fillId",fill);xf.SetAttributeValue("applyFill",1);
                            style=xfs.Elements().Count();xfs.Add(xf);map.Add(pair,style);
                        }
                        node.SetAttributeValue("s",style);
                    }
                    changed.Add(parts[group.Key],sheet);
                }
                fills.SetAttributeValue("count",fills.Elements().Count());xfs.SetAttributeValue("count",xfs.Elements().Count());changed.Add("xl/styles.xml",styles);
            }
            using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))foreach(var entry in input.Entries)
            {
                var next=zip.CreateEntry(entry.FullName,CompressionLevel.Optimal);next.LastWriteTime=new(2000,1,1,0,0,0,TimeSpan.Zero);using var target=next.Open();
                if(changed.TryGetValue(entry.FullName,out var xml)){var xmlBytes=Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));target.Write(xmlBytes);}
                else{using var stream=entry.Open();stream.CopyTo(target);}
            }
            var bytes=memory.ToArray();
            if(File.Exists(output)){if(!File.ReadAllBytes(output).SequenceEqual(bytes))throw new InvalidDataException("TYPED_COLOR_OUTPUT_COLLISION");}
            else{using var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read);file.Write(bytes);file.Flush(true);}
        }
        using(var input=ZipFile.OpenRead(source.Path))using(var exported=ZipFile.OpenRead(output))
        {
            if(!input.Entries.Select(e=>e.FullName).SequenceEqual(exported.Entries.Select(e=>e.FullName)))throw new InvalidDataException("TYPED_EXPORT_PACKAGE_PARTS_CHANGED");
            var sheets=WorkbookProfiles.SheetParts(input);
            foreach(var entry in input.Entries)
            {
                var owner=sheets.SingleOrDefault(p=>p.Value==entry.FullName).Key;
                var permitted=cells.Where(c=>c.Sheet==owner).Select(c=>c.Coordinate).ToHashSet(StringComparer.Ordinal);
                if(entry.FullName=="xl/styles.xml"&&cells.Length>0)
                {
                    var a=WorkbookProfiles.Xml(input,entry.FullName);var b=WorkbookProfiles.Xml(exported,entry.FullName);
                    foreach(var element in new[]{"fills","cellXfs"})
                    {
                        var left=a.Root!.Element(s+element)!;var right=b.Root!.Element(s+element)!;
                        right.Elements().Skip(left.Elements().Count()).Remove();right.SetAttributeValue("count",left.Attribute("count")!.Value);
                    }
                    if(!XNode.DeepEquals(a,b))throw new InvalidDataException("TYPED_EXPORT_ORIGINAL_STYLES_CHANGED");
                }
                else if(permitted.Count>0)
                {
                    var a=WorkbookProfiles.Xml(input,entry.FullName);var b=WorkbookProfiles.Xml(exported,entry.FullName);
                    foreach(var xml in new[]{a,b})foreach(var cell in xml.Descendants(s+"c").Where(c=>permitted.Contains((string)c.Attribute("r")!)))cell.Attribute("s")?.Remove();
                    if(!XNode.DeepEquals(a,b))throw new InvalidDataException("TYPED_EXPORT_VALUES_FORMULAS_LAYOUT_CHANGED");
                }
                else
                {
                    using var left=entry.Open();using var right=exported.GetEntry(entry.FullName)!.Open();
                    if(!System.Security.Cryptography.SHA256.HashData(left).SequenceEqual(System.Security.Cryptography.SHA256.HashData(right)))throw new InvalidDataException("TYPED_EXPORT_UNTOUCHED_PART_CHANGED");
                }
            }
        }
        var manifest=output+".json";
        NativeAttendanceCoordinator.WriteExact(manifest,new{Contract="F13_TYPED_COLOR_EXPORT_ADAPTER_V1",Source=source,Output=Pin(output),Cells=cells,Catalog=SharedNoveltyCatalog.Fingerprint,
            ValuesAndFormulasUnchanged=true,OriginalPartsExceptStylesAndTypedCellSheetsUnchanged=true,LabourRecalculated=false,EconomicRecalculated=false});
        return new(Pin(output),Pin(manifest),cells);
    }
}
