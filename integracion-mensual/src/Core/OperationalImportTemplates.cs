using System.Text;
using System.Xml.Linq;
using SistemaMarcaciones.Monthly.Controlled;
using SistemaMarcaciones.Monthly.Projects;

namespace SistemaMarcaciones.F13.Operational;

public static class OperationalImportTemplates
{
    public static string Label(DocumentModule module)=>module switch{DocumentModule.COMMISSION=>"Comisiones",DocumentModule.PERSONAL_PERMISSION=>"Permisos_personales",DocumentModule.MEDICAL_PERMISSION=>"Permisos_medicos",DocumentModule.JUSTIFICATION=>"Justificaciones",DocumentModule.VACATION=>"Vacaciones",DocumentModule.LEAVE=>"Licencias",DocumentModule.GUARD_CHANGE=>"Cambios_Guardia",DocumentModule.CFE=>"CFE",DocumentModule.MANUAL_ATTENDANCE=>"Marcaciones_manuales",_=>"ECU_911"};
    static readonly XNamespace S="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace R="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace P="http://schemas.openxmlformats.org/package/2006/relationships";
    static byte[] B(XElement x)=>Encoding.UTF8.GetBytes(new XDocument(x).ToString(SaveOptions.DisableFormatting));
    public static void Write(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach(var module in Enum.GetValues<DocumentModule>())
        {
            var schema=DocumentImportSchemas.For(module);XNamespace ct="http://schemas.openxmlformats.org/package/2006/content-types";
            var parts=new Dictionary<string,byte[]>{
                ["[Content_Types].xml"]=B(new XElement(ct+"Types",new XElement(ct+"Default",new XAttribute("Extension","rels"),new XAttribute("ContentType","application/vnd.openxmlformats-package.relationships+xml")),new XElement(ct+"Default",new XAttribute("Extension","xml"),new XAttribute("ContentType","application/xml")),new XElement(ct+"Override",new XAttribute("PartName","/xl/workbook.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),new XElement(ct+"Override",new XAttribute("PartName","/xl/worksheets/sheet1.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")),new XElement(ct+"Override",new XAttribute("PartName","/xl/styles.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")))),
                ["_rels/.rels"]=B(new XElement(P+"Relationships",new XElement(P+"Relationship",new XAttribute("Id","rId1"),new XAttribute("Type",R.NamespaceName+"/officeDocument"),new XAttribute("Target","xl/workbook.xml")))),
                ["xl/workbook.xml"]=B(new XElement(S+"workbook",new XAttribute(XNamespace.Xmlns+"r",R),new XElement(S+"bookViews",new XElement(S+"workbookView")),new XElement(S+"sheets",new XElement(S+"sheet",new XAttribute("name","DATOS"),new XAttribute("sheetId",1),new XAttribute(R+"id","rId1"))))),
                ["xl/_rels/workbook.xml.rels"]=B(new XElement(P+"Relationships",new XElement(P+"Relationship",new XAttribute("Id","rId1"),new XAttribute("Type",R.NamespaceName+"/worksheet"),new XAttribute("Target","worksheets/sheet1.xml")),new XElement(P+"Relationship",new XAttribute("Id","styles"),new XAttribute("Type",R.NamespaceName+"/styles"),new XAttribute("Target","styles.xml")))),
                ["xl/styles.xml"]=B(new XElement(S+"styleSheet",new XElement(S+"fonts",new XAttribute("count",1),new XElement(S+"font",new XElement(S+"sz",new XAttribute("val",11)),new XElement(S+"name",new XAttribute("val","Calibri")))),new XElement(S+"fills",new XAttribute("count",2),new XElement(S+"fill",new XElement(S+"patternFill",new XAttribute("patternType","none"))),new XElement(S+"fill",new XElement(S+"patternFill",new XAttribute("patternType","gray125")))),new XElement(S+"borders",new XAttribute("count",1),new XElement(S+"border")),new XElement(S+"cellStyleXfs",new XAttribute("count",1),new XElement(S+"xf",new XAttribute("numFmtId",0),new XAttribute("fontId",0),new XAttribute("fillId",0),new XAttribute("borderId",0))),new XElement(S+"cellXfs",new XAttribute("count",2),new XElement(S+"xf",new XAttribute("numFmtId",0),new XAttribute("fontId",0),new XAttribute("fillId",0),new XAttribute("borderId",0),new XAttribute("xfId",0)),new XElement(S+"xf",new XAttribute("numFmtId",49),new XAttribute("fontId",0),new XAttribute("fillId",0),new XAttribute("borderId",0),new XAttribute("xfId",0),new XAttribute("applyNumberFormat",1))),new XElement(S+"cellStyles",new XAttribute("count",1),new XElement(S+"cellStyle",new XAttribute("name","Normal"),new XAttribute("xfId",0),new XAttribute("builtinId",0)))))
            };
            var sheet=new XElement(S+"worksheet",new XElement(S+"dimension",new XAttribute("ref","A1:"+MatrixCoordinates.A1(1,schema.Columns.Length))),
                new XElement(S+"sheetViews",new XElement(S+"sheetView",new XAttribute("workbookViewId",0))),
                new XElement(S+"cols",schema.Columns.Select((c,i)=>new XElement(S+"col",new XAttribute("min",i+1),new XAttribute("max",i+1),new XAttribute("width",24),new XAttribute("customWidth",1),new XAttribute("style",c.Text?1:0)))),
                new XElement(S+"sheetData",new XElement(S+"row",new XAttribute("r",1),schema.Columns.Select((c,i)=>new XElement(S+"c",new XAttribute("r",MatrixCoordinates.A1(1,i+1)),new XAttribute("t","inlineStr"),new XElement(S+"is",new XElement(S+"t",c.Header)))))));
            parts["xl/worksheets/sheet1.xml"]=B(sheet);OperationalArtifacts.WritePackage(Path.Combine(folder,Label(module)+".xlsx"),parts);
        }
        var readme=Path.Combine(folder,"LEEME_CARGAS.txt");
        if(!File.Exists(readme))File.WriteAllText(readme,"PERSONAL Y JORNADAS\r\nUna fila por persona y vigencia; una jornada por persona y fecha. Cédulas y códigos biométricos se conservan como texto. Poblaciones: Administrativo, Operativo administrativo, Director, Guardia. Estado de jornada: Confirmada, con referencia documental. No se infieren guardias ni descansos.\r\n\r\nDOCUMENTOS\r\n"+string.Join("\r\n",DocumentImportSchemas.For(DocumentModule.PERSONAL_PERMISSION).Instructions)+"\r\nLos estados desconocidos deben mantenerse como desconocidos. La carga de ECU 911, cambios de Guardia y la reincorporación exacta de vacaciones no equivale a su aplicación laboral: la interfaz muestra los casos pendientes de su contrato específico.\r\n",new UTF8Encoding(true));
    }
}
