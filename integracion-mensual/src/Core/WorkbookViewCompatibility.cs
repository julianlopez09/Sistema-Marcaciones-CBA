using System.Xml.Linq;

namespace SistemaMarcaciones.F13.Integrated.Native;

/// <summary>New export metadata only; never repairs or overwrites a sealed source workbook.</summary>
public static class WorkbookViewCompatibility
{
    public const string Contract = "F13_NEW_EXPORT_PRIMARY_WORKBOOK_VIEW_V1";
    static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    // sheetView@workbookViewId is an index into bookViews/workbookView. The working
    // sheet refers to zero, so that parent view must exist in the new workbook.
    public static void EnsurePrimaryView(XDocument workbook)
    {
        var root = workbook.Root;
        if (root?.Name != S + "workbook") throw new InvalidDataException("EXPORT_WORKBOOK_ROOT_REQUIRED");
        var sheets = root.Elements(S + "sheets").ToArray();
        if (sheets.Length != 1 || !sheets[0].Elements(S + "sheet").Any())
            throw new InvalidDataException("EXPORT_SINGLE_NONEMPTY_SHEETS_REQUIRED");
        var collections = root.Elements(S + "bookViews").ToArray();
        if (collections.Length > 1) throw new InvalidDataException("EXPORT_DUPLICATE_BOOK_VIEWS");
        var views = collections.SingleOrDefault();
        if (views == null)
        {
            views = new XElement(S + "bookViews");
            sheets[0].AddBeforeSelf(views);
        }
        else if (views.ElementsAfterSelf().All(e => e != sheets[0]))
            throw new InvalidDataException("EXPORT_BOOK_VIEWS_ORDER_INVALID");
        if (!views.Elements(S + "workbookView").Any())
            views.Add(new XElement(S + "workbookView", new XAttribute("activeTab", 0), new XAttribute("firstSheet", 0)));
    }
}
