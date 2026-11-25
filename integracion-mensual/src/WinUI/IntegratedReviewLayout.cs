using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SistemaMarcaciones.Desktop.Pilot.WinUI;

internal sealed partial class IntegratedWindow
{
    static void PlaceTableAndDetails(Grid root, InstitutionalTable table, ScrollViewer detail)
    {
        detail.MaxHeight = 260;
        var panel = new Grid { RowSpacing = 8 };
        panel.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.Children.Add(table.Root);

        var details = new Expander
        {
            Header = "Detalle y acciones",
            Content = detail,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetRow(details, 1);
        panel.Children.Add(details);
        Grid.SetRow(panel, 2);
        root.Children.Add(panel);
    }

    static string PopulationLabel(string population) => population switch
    {
        "ADMIN" => "Administrativo",
        "OPADMIN" => "Operativo en labores administrativas",
        "DIRECTORES" => "Directores",
        "GUARDIA" => "Guardia operativa",
        _ => population
    };

    static string ActionLabel(string label) => label switch
    {
        "Importar biométrico QA admitido" => "Cargar datos de ejemplo",
        "Procesar C# QA" or "Procesar motor específico QA" => "Procesar marcaciones",
        "Generar matriz principal" or "Generar matriz de la población" => "Generar matriz",
        "Abrir editor independiente" => "Abrir matriz",
        "Reanudar" => "Actualizar vista",
        "Exportar revisión guardada QA" or "Exportar matriz y revisión QA" => "Exportar a Excel",
        _ => label
    };

    static string ResultLabel(string value) => value switch
    {
        "PENDIENTE_JUSTIFICACION" => "Pendiente de justificación",
        "PENDIENTE_REGULARIZACION_DOCUMENTAL" => "Pendiente de regularización documental",
        "F13_RENDERED_PREVIEW_XLSX_NOT_CERTIFIED" => "Matriz de prueba disponible",
        "COMPLETED_NO_NOVELTIES" => "Sin novedades",
        "COMPLETED_WITH_RESULTS" => "Con resultados",
        "BLOCKED_MISSING_EVIDENCE" or "BLOCKED" => "Falta evidencia",
        "ADMINISTRATIVO" => "Administrativo",
        "OPERATIVO_EN_LABORES_ADMINISTRATIVAS" => "Operativo en labores administrativas",
        "OPERATIVO" => "Operativo",
        "INDETERMINADO" => "No determinado",
        _ => value
    };

    static TextBlock TechnicalDetail(StackPanel parent)
    {
        var body = Text("", 11);
        body.IsTextSelectionEnabled = true;
        parent.Children.Add(new Expander
        {
            Header = "Datos técnicos",
            Content = body,
            HorizontalAlignment = HorizontalAlignment.Stretch
        });
        return body;
    }
}
