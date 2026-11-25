using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
namespace SistemaMarcaciones.Desktop.Pilot.WinUI;

// F12 layout only: wrap whole controls, retaining native resources and accessible names.
internal sealed class PilotWrapPanel:Panel
{
    private const double Gap=8;
    protected override Size MeasureOverride(Size available)
    {
        var limit=double.IsFinite(available.Width)?available.Width:double.PositiveInfinity;
        double x=0,y=0,lineHeight=0,maxWidth=0;
        foreach(var child in Children)
        {
            child.Measure(new Size(limit,available.Height));var size=child.DesiredSize;
            if(x>0&&x+size.Width>limit){maxWidth=Math.Max(maxWidth,x-Gap);y+=lineHeight+Gap;x=0;lineHeight=0;}
            x+=size.Width+Gap;lineHeight=Math.Max(lineHeight,size.Height);
        }
        return new Size(Math.Min(limit,Math.Max(maxWidth,Math.Max(0,x-Gap))),y+lineHeight);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double x=0,y=0,lineHeight=0;
        foreach(var child in Children)
        {
            var size=child.DesiredSize;
            if(x>0&&x+size.Width>finalSize.Width){y+=lineHeight+Gap;x=0;lineHeight=0;}
            child.Arrange(new Rect(x,y,size.Width,size.Height));x+=size.Width+Gap;lineHeight=Math.Max(lineHeight,size.Height);
        }
        return finalSize;
    }
}
