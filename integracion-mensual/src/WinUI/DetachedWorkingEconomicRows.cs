using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SistemaMarcaciones.F13.Integrated.Native;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Monthly.Projects;

namespace SistemaMarcaciones.Desktop.Pilot.WinUI;
public sealed partial class DetachedExcelWindow
{
    WorkingEconomicRows? workingRows;
    WorkingEconomicRow? selectedWorkingRow;
    bool WorkingRowsVisible=>!historicalReference&&layer.SelectedIndex==0&&workingRows?.EconomicSheets.Contains(sheet,StringComparer.Ordinal)==true;
    void RefreshWorkingRowsView()
    {
        if(!WorkingRowsVisible){if(grid!=null)viewport.Child=grid.View;return;}
        var projection=workingRows!;var panel=new Grid{RowSpacing=10};
        selectedWorkingRow=projection.Rows.SingleOrDefault(r=>r.PersonKey==selectedWorkingRow?.PersonKey);
        panel.KeyDown+=async(_,e)=>
        {
            if(!PhysicalSheetCanvas.Modifier(Windows.System.VirtualKey.Control))return;
            Func<Task>? command=e.Key switch{Windows.System.VirtualKey.Z=>()=>History(false),Windows.System.VirtualKey.Y=>()=>History(true),Windows.System.VirtualKey.C=>Copy,Windows.System.VirtualKey.S=>Save,_=>null};
            if(command!=null){e.Handled=true;await Safe(command);}
        };
        panel.RowDefinitions.Add(new(){Height=GridLength.Auto});panel.RowDefinitions.Add(new(){Height=GridLength.Auto});panel.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});
        panel.Children.Add(InstitutionalVisualTokens.Text("Resumen de atrasos en revisión",18,true));
        var explanation=InstitutionalVisualTokens.Text($"{projection.Rows.Length} servidor(es) con atrasos conocidos · {projection.ExcludedPeople} sin atrasos conocidos en esta vista · revisión {projection.Revision}\n"+(profile.Draft.Synthetic?"Datos de ejemplo · ":"")+"Revisión pendiente de acreditación. No se calculan descuentos. Los valores originales se consultan en «Más opciones de revisión y ayuda».",12);
        Grid.SetRow(explanation,1);panel.Children.Add(explanation);
        var table=new InstitutionalTable("Valores a descontar: filas dinámicas de revisión manual, no acreditadas");
        table.Configure(new("Servidor",180),new("Fechas revisadas",250),new("Minutos en revisión",150),new("Valor original (referencia)",190),new("Estado económico",330));
        table.Rows(projection.Rows.Select(r=>new PresentationRow(r.PersonKey,[r.Name,r.DateLabel,r.Minutes.ToString(CultureInfo.InvariantCulture),r.OriginalAmount,EconomicLabel(r.EconomicState)],r)));
        table.Selection+=row=>
        {
            selectedWorkingRow=row?.Source as WorkingEconomicRow;setting=true;address.Text="REVISIÓN";content.IsReadOnly=true;
            content.Text=selectedWorkingRow==null?"":$"{selectedWorkingRow.Name} · {selectedWorkingRow.DateLabel} · {selectedWorkingRow.Minutes} minuto(s) en revisión, NO acreditados";setting=false;
        };
        Grid.SetRow(table.Root,2);panel.Children.Add(table.Root);viewport.Child=panel;
        content.IsReadOnly=true;cellDetail.Text=$"Filas dinámicas de revisión · {projection.Rows.Length} servidor(es) · NO ACREDITADA · coordenadas fuente conservadas en el detalle";
        PilotLog.Event("WORKING_ECONOMIC_ROWS_UPDATED",$"Revision={projection.Revision};Rows={projection.Rows.Length};Excluded={projection.ExcludedPeople};UnknownDates={projection.UnknownPersonDates};EconomicRecalculated=False");
    }
    static string EconomicLabel(string value)=>value switch
    {
        "ORIGINAL CONSERVADO; SIN NUEVO CÁLCULO"=>"Importe original conservado",
        "IMPACTO ECONÓMICO POR DETERMINAR"=>"Pendiente de evaluación económica",
        _=>value
    };
    async Task WorkingRowsDetail()
    {
        var row=selectedWorkingRow;
        var detail=row==null?"Selecciona una fila de la revisión de trabajo. La vista original conserva la tabla y sus fórmulas.":JsonSerializer.Serialize(new{Row=row,Warning=WorkingEconomicRows.Warning},SistemaMarcaciones.Monthly.Native.Outcomes.B07Contract.Wire);
        var text=InstitutionalVisualTokens.Text(detail,12);text.IsTextSelectionEnabled=true;
        await ShowTrackedDialog(new ContentDialog{Title="Persona y fechas de revisión manual · NO ACREDITADA",Content=new ScrollViewer{Content=text,MaxHeight=550},CloseButtonText="Cerrar",XamlRoot=root.XamlRoot,RequestedTheme=root.ActualTheme});
    }
    bool IsTypedLate(string coordinate)=>layer.SelectedIndex==0&&workingRows?.Rows.SelectMany(r=>r.Dates).Any(d=>d.Sheet==sheet&&d.Coordinate==coordinate&&d.Origin==TypedLateReview.Kind)==true;
    string WorkingRowsClipboard()=>workingRows==null?"":string.Join("\n",new[]{"Servidor\tFechas revisadas\tMinutos de revisión\tValor original (referencia)\tEstado"}.Concat(workingRows.Rows.Select(r=>$"{r.Name}\t{r.DateLabel}\t{r.Minutes.ToString(CultureInfo.InvariantCulture)}\t{r.OriginalAmount}\t{r.EconomicState}")));
}
