using System.Text.Json;
namespace SistemaMarcaciones.Desktop.Pilot.WinUI;
internal static class PilotLog
{
    public static string? Root;static readonly string Id=Guid.NewGuid().ToString("N");
    public static void Event(string code,string detail="",double ms=0)
    {if(Root==null)return;Directory.CreateDirectory(Root);File.AppendAllText(Path.Combine(Root,"UI_"+Id+".jsonl"),JsonSerializer.Serialize(new{At=DateTimeOffset.UtcNow,Code=code,Detail=detail,Milliseconds=ms})+"\n");}
}
