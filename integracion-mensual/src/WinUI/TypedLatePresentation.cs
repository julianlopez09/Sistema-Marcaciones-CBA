using SistemaMarcaciones.Domain.Modelo;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Presentation.Contracts;

namespace SistemaMarcaciones.Desktop.Pilot.WinUI;

// Presentation only: the caller verifies the active typed person/date witness.
// Do not classify arbitrary numeric text, change the frozen F2 classifier, or approve labour results.
internal static class TypedLatePresentation
{
    internal static ManualCellPresentation For(ManualCellRevision edit,bool verifiedTypedLate)=>verifiedTypedLate
        ?new(edit.ChoiceId,SharedNoveltyCatalog.Color(nameof(CategoriaNovedad.atraso)),SharedNoveltyCatalog.Fingerprint)
        :ManualPresentation.For(edit);
}
