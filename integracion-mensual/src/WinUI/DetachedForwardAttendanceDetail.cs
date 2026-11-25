using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SistemaMarcaciones.Monthly.ForwardAttendance;
using SistemaMarcaciones.F13.Integrated.Native;
using SistemaMarcaciones.Monthly.Native.Outcomes;
namespace SistemaMarcaciones.Desktop.Pilot.WinUI;
public sealed partial class DetachedExcelWindow
{
    async Task ForwardDetail()
    {
        if(WorkingRowsVisible){await WorkingRowsDetail();return;}
        if(grid==null)return;
        if(SpecificCausalDetail is {} inspect&&await inspect(sheet,grid.Coordinate) is {} received)
        {
            var summary=InstitutionalVisualTokens.Text(received.Summary,14);summary.IsTextSelectionEnabled=true;
            var technical=InstitutionalVisualTokens.Text(received.Technical,11);technical.IsTextSelectionEnabled=true;
            var panel=new StackPanel{Spacing=12};panel.Children.Add(summary);panel.Children.Add(new Expander{Header="Referencia técnica de origen",Content=technical,HorizontalContentAlignment=HorizontalAlignment.Stretch});
            await ShowTrackedDialog(new ContentDialog{XamlRoot=root.XamlRoot,Title="Detalle de la jornada",CloseButtonText="Cerrar",Content=new ScrollViewer{Content=panel,MaxHeight=560}});return;
        }
        var selectedSheet=sheet;var selectedCoordinate=grid.Coordinate;var selectedMatrix=MatrixId;var work=project.Work;
        var text=await Task.Run(()=>
        {
            var population=SistemaMarcaciones.F13.Populations.MultiPopulationSession.Detail(work,selectedMatrix,selectedSheet,selectedCoordinate);
            if(population!=null)return JsonSerializer.Serialize(population,B07Contract.Wire)+"\nMotor específico de población. Revisión manual separada; no modifica resultados ni importes.";
            var native=NativeAttendanceCoordinator.Detail(work,selectedMatrix,selectedSheet,selectedCoordinate);
            if(native!=null)return JsonSerializer.Serialize(native,B07Contract.Wire)+"\nRevisión manual separada; no modifica resultados ni importes.";
            var historical=ForwardAttendanceCoordinator.Detail(work,selectedMatrix,selectedSheet,selectedCoordinate);
            return historical==null?"Trazabilidad causal no disponible para esta celda. No se inventa una explicación.":
                JsonSerializer.Serialize(historical,B07Contract.Wire)+"\nRevisión manual separada; no modifica resultados ni importes.";
        });
        var content=InstitutionalVisualTokens.Text(text,12);content.IsTextSelectionEnabled=true;
        var scroll=new ScrollViewer{Content=content,VerticalScrollMode=ScrollMode.Enabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,IsVerticalScrollChainingEnabled=false,IsTabStop=true};
        Name(scroll,"Contenido causal desplazable; el botón Cerrar permanece fuera del desplazamiento");
        var dialog=new ContentDialog{XamlRoot=root.XamlRoot,Title="Persona / fecha · Todos los resultados causales",CloseButtonText="Cerrar",Content=scroll};
        Name(dialog,"Detalle de todos los resultados, documentos aplicados y trazas de la celda seleccionada");
        void FitViewport()=>scroll.MaxHeight=Math.Clamp(root.ActualHeight-200d,120d,560d);
        SizeChangedEventHandler resized=(_,_)=>FitViewport();
        FitViewport();root.SizeChanged+=resized;
        try{await ShowTrackedDialog(dialog);}
        finally{root.SizeChanged-=resized;}
    }
}
