using System.Globalization;
using System.Text.Json;

namespace SistemaMarcaciones.F13.Operational;

/// <summary>Formats received results. Multiple unresolved results are never silently summed or assigned a priority.</summary>
public static class OperationalPresentation
{
    static bool DocumentaryOnly(JsonElement row)=>row.TryGetProperty("Legacy",out var legacy)&&legacy.TryGetProperty("Matriz",out var matrix)
        &&matrix.ValueKind==JsonValueKind.String&&matrix.GetString()=="REGULARIZACION_DOCUMENTAL";
    public static string Value(OperationalCase[] cases,JsonElement[] raw)
    {
        if(cases.Length==0)return "0";
        var ordinary=cases.Where((c,i)=>!DocumentaryOnly(raw[i])).ToArray();
        if(ordinary.Length==0)return "0"; // Backed interval differences remain in the separate documentary review.
        if(ordinary.Length>1)return "VER DETALLE ("+ordinary.Length+")";
        var c=ordinary[0];
        var missing=c.Type switch
        {
            "FALTA_INGRESO_MANANA" or "GUARDIA_FALTA_MARCACION_INGRESO"=>"NO REGISTRA INGRESO A LA INSTITUCIÓN",
            "GUARDIA_FALTA_MARCACION_SALIDA"=>"NO REGISTRA LA SALIDA DE LA INSTITUCIÓN",
            "MARCACION_FALTANTE" or "SIN_MARCACIONES" or "GUARDIA_SIN_MARCACIONES"=>"NO REGISTRA MARCACIONES",
            "FALTA_MARCACION_RETORNO_EVENTO"=>"NO REGISTRA EL RETORNO DEL INTERVALO AUTORIZADO",
            "FALTA_MARCACION_SALIDA_EVENTO"=>"NO REGISTRA LA SALIDA DEL INTERVALO AUTORIZADO",
            _=>null
        };
        if(missing!=null)return missing;
        if(c.Type is "MARCACIONES_ADICIONALES" or "GUARDIA_MARCACIONES_ADICIONALES" or "MARCACION_EXTRA_CON_COBERTURA")return "MARCACIONES ADICIONALES";
        return c.Minutes>0?c.Minutes.Value.ToString(CultureInfo.InvariantCulture):"VER DETALLE";
    }
}
