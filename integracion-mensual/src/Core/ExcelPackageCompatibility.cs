using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Monthly.Host.NativeIntegration;
using SistemaMarcaciones.Workspace.StorageV2;

namespace SistemaMarcaciones.F13.Integrated.Native;

public sealed record ExcelCompatibilityReceipt(B09File Workbook,B09File Manifest,string[] ChangedParts);
/// <summary>New presentation copies only. Never rewrites frozen exports or calculates cells.</summary>
public static class ExcelPackageCompatibility
{
    public const string Contract="F13_SR3_EXCEL_PRIMARY_VIEW_AND_COLUMN_LIMIT_V1";
    static readonly XNamespace S="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public const int MaximumColumns=16384;
    public static bool ClampTrailingColumnMetadata(XDocument worksheet)
    {
        if(worksheet.Root?.Name!=S+"worksheet")throw new InvalidDataException("EXCEL_WORKSHEET_ROOT_REQUIRED");
        // Validate first: real cells beyond the Excel grid are never clipped or removed.
        foreach(var cell in worksheet.Descendants(S+"c"))
        {
            var reference=(string?)cell.Attribute("r")??throw new InvalidDataException("CELL_REFERENCE_REQUIRED");
            int i=0,column=0;while(i<reference.Length&&reference[i] is >= 'A' and <= 'Z')
            {column=checked(column*26+reference[i]-'A'+1);i++;}
            if(column is <1 or >MaximumColumns||!int.TryParse(reference.AsSpan(i),NumberStyles.None,CultureInfo.InvariantCulture,out var row)||row is <1 or >1048576)
                throw new InvalidDataException("EXCEL_REAL_CELL_OUTSIDE_GRID_NO_PAYLOAD_REPAIR");
        }
        var collections=worksheet.Root.Elements(S+"cols").ToArray();
        if(collections.Length>1)throw new InvalidDataException("EXCEL_DUPLICATE_COLUMN_COLLECTION");
        var edits=new List<XElement>();
        foreach(var col in collections.SelectMany(c=>c.Elements(S+"col")))
        {
            if(!uint.TryParse((string?)col.Attribute("min"),NumberStyles.None,CultureInfo.InvariantCulture,out var min)
                ||!uint.TryParse((string?)col.Attribute("max"),NumberStyles.None,CultureInfo.InvariantCulture,out var max)
                ||min is <1 or >MaximumColumns||max<min)
                throw new InvalidDataException("EXCEL_COLUMN_RANGE_NOT_SAFELY_CLIPPABLE");
            if(max>MaximumColumns)edits.Add(col);
        }
        // Keep every width, style, hidden flag and overlapping definition as supplied.
        // Only the non-addressable tail beyond XFD is reduced to Excel's grid limit.
        foreach(var col in edits)col.SetAttributeValue("max",MaximumColumns);
        return edits.Count>0;
    }
    static string Hash(ZipArchiveEntry e){using var s=e.Open();return Convert.ToHexString(SHA256.HashData(s));}
    static byte[] Xml(XDocument d)=>Encoding.UTF8.GetBytes(d.ToString(SaveOptions.DisableFormatting));
    public static ExcelCompatibilityReceipt Create(B09File source,string destination,string label)
    {
        CompletionExports.Verify(source);destination=PathSafety.Sandbox(destination);PathSafety.CheckLinks(destination);
        if(label.Length==0||label.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_'))throw new InvalidDataException("EXCEL_COMPATIBILITY_SAFE_LABEL");
        var id=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Contract+"\n"+label+"\n"+source.SHA256)));
        Directory.CreateDirectory(destination);var output=Path.Combine(destination,$"EXCEL_COMPATIBLE_{label}_{id[..16]}.xlsx");
        HistoricalFinalMatrices.RequireNewDestination(output);PathSafety.CheckLinks(output);
        var changes=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        using var input=ZipFile.OpenRead(source.Path);
        var book=WorkbookProfiles.Xml(input,"xl/workbook.xml");var originalBook=new XDocument(book);
        WorkbookViewCompatibility.EnsurePrimaryView(book);
        if(!XNode.DeepEquals(book,originalBook))changes.Add("xl/workbook.xml",Xml(book));
        foreach(var entry in input.Entries.Where(e=>e.FullName.StartsWith("xl/worksheets/",StringComparison.Ordinal)&&e.FullName.EndsWith(".xml",StringComparison.Ordinal)))
        {
            var sheet=WorkbookProfiles.Xml(input,entry.FullName);
            if(ClampTrailingColumnMetadata(sheet))changes.Add(entry.FullName,Xml(sheet));
        }
        using var memory=new MemoryStream();
        using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))foreach(var entry in input.Entries)
        {
            var next=zip.CreateEntry(entry.FullName,CompressionLevel.Optimal);next.LastWriteTime=entry.LastWriteTime;using var to=next.Open();
            if(changes.TryGetValue(entry.FullName,out var b))to.Write(b);else{using var from=entry.Open();from.CopyTo(to);}
        }
        var bytes=memory.ToArray();
        if(File.Exists(output)){if(!File.ReadAllBytes(output).AsSpan().SequenceEqual(bytes))throw new InvalidDataException("EXCEL_COMPATIBLE_SEALED_OUTPUT_CHANGED");}
        else{using var f=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read);f.Write(bytes);f.Flush(true);}
        using(var verified=ZipFile.OpenRead(output))
        {
            if(!input.Entries.Select(e=>e.FullName).SequenceEqual(verified.Entries.Select(e=>e.FullName)))throw new InvalidDataException("EXCEL_COMPATIBILITY_PART_SET_CHANGED");
            foreach(var part in input.Entries.Where(e=>!changes.ContainsKey(e.FullName)))
                if(Hash(part)!=Hash(verified.GetEntry(part.FullName)!))throw new InvalidDataException("EXCEL_COMPATIBILITY_UNTOUCHED_PART_CHANGED");
        }
        var manifest=output+".json";NativeAttendanceCoordinator.WriteExact(manifest,new{Contract,Source=source,Output=CompletionExports.Pin(output),ChangedParts=changes.Keys.ToArray(),
            OriginalOverlappingDefinitionsPreserved=true,OnlyNonAddressableColumnTailClipped=true,CellsFormulasCachesStylesMergesPreserved=true,NoRecalculation=true,NoInstitutionalCertification=true});
        return new(CompletionExports.Pin(output),CompletionExports.Pin(manifest),changes.Keys.ToArray());
    }
}
