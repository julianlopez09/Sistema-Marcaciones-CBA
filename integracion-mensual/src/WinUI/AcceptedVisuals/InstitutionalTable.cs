using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Markup;
namespace SistemaMarcaciones.Desktop.Pilot.WinUI;

internal sealed record PresentationRow(string Key,string[] Cells,object Source,string Color="Transparent")
{
    public string C0=>Cells.ElementAtOrDefault(0)??"—";public string C1=>Cells.ElementAtOrDefault(1)??"—";
    public string C2=>Cells.ElementAtOrDefault(2)??"—";public string C3=>Cells.ElementAtOrDefault(3)??"—";
    public string C4=>Cells.ElementAtOrDefault(4)??"—";public string C5=>Cells.ElementAtOrDefault(5)??"—";
    public string C6=>Cells.ElementAtOrDefault(6)??"—";
    public string C7=>Cells.ElementAtOrDefault(7)??"—";public string C8=>Cells.ElementAtOrDefault(8)??"—";
    public string C9=>Cells.ElementAtOrDefault(9)??"—";public string C10=>Cells.ElementAtOrDefault(10)??"—";
    public override string ToString()=>string.Join(" · ",Cells);
}
internal sealed record PresentationColumn(string Label,double Width,bool Numeric=false,int? CategoryIndex=null);

// One shared scroll viewport: header stays vertically fixed while every column scrolls together.
// Standard ItemsStackPanel/ListView virtualization and keyboard selection are retained.
internal sealed class InstitutionalTable
{
    public Grid Root {get;}=new();
    public ListView List {get;}=new(){SelectionMode=ListViewSelectionMode.Single,IsItemClickEnabled=false};
    public event Action<int>? Sort;
    public event Action<PresentationRow>? Open;
    public event Action<PresentationRow?>? Selection;
    public PresentationRow? Selected=>List.SelectedItem as PresentationRow;
    private readonly Grid tableRegion=new(){HorizontalAlignment=HorizontalAlignment.Left};
    private readonly StackPanel header=new(){Orientation=Orientation.Horizontal};
    private PresentationColumn[] columns=[];
    private double[] widths=[];
    private double[] minimumWidths=[];
    private bool manualWidths;
    private readonly List<FontIcon> sortIcons=[];
    private readonly TextBlock empty=InstitutionalVisualTokens.Text("Sin registros para estos filtros. Ajusta la búsqueda o el período.",14);
    public InstitutionalTable(string name)
    {
        AutomationProperties.SetName(List,name+". Tabla de consulta; flechas para seleccionar, Enter para detalle.");
        List.ItemContainerStyle=(Style)Microsoft.UI.Xaml.Application.Current.Resources["InstitutionalRow"];
        InstitutionalVisualTokens.Brush(header,Panel.BackgroundProperty,"SurfaceSecondary");
        tableRegion.RowDefinitions.Add(new(){Height=new(32)});tableRegion.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});
        tableRegion.Children.Add(header);Grid.SetRow(List,1);tableRegion.Children.Add(List);
        var viewport=new ScrollViewer{Content=tableRegion,HorizontalScrollMode=ScrollMode.Enabled,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalContentAlignment=HorizontalAlignment.Left,VerticalScrollMode=ScrollMode.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled};
        AutomationProperties.SetName(viewport,"Desplazamiento horizontal de encabezados y tabla");Root.Children.Add(viewport);
        var blank=new StackPanel{Spacing=8,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        var icon=new FontIcon{Glyph="\uE8A5",FontFamily=new("Segoe Fluent Icons"),FontSize=28};blank.Children.Add(icon);blank.Children.Add(empty);Root.Children.Add(blank);
        empty.Tag=blank;
        List.SelectionChanged+=(_,_)=>Selection?.Invoke(Selected);
        // Shared attached styles keep ThemeResource expressions live on recycled rows.
        List.ContainerContentChanging+=(_,e)=>{if(!e.InRecycleQueue)e.ItemContainer.Style=(Style)Microsoft.UI.Xaml.Application.Current.Resources[e.ItemIndex%2==0?"InstitutionalRow":"InstitutionalRowAlternate"];};
        Root.SizeChanged+=(_,e)=>FitWidth(e.NewSize.Width);
        List.DoubleTapped+=(_,_)=>{if(Selected is {} row)Open?.Invoke(row);};
        // Native ListViewItem handles Enter before the ordinary bubbling subscription.
        // Listen to that same routed event even when handled; open only the selected source.
        List.AddHandler(UIElement.KeyDownEvent,new Microsoft.UI.Xaml.Input.KeyEventHandler((_,e)=>
        {if(e.Key==Windows.System.VirtualKey.Enter&&Selected is {} row){Open?.Invoke(row);e.Handled=true;}}),true);
        List.Loaded+=(_,_)=>PilotLog.Event("TABLE_VIEWPORT",$"DIP={List.ActualHeight:F0};RowDIP=32;Items={List.Items.Count};VisibleCountRequiresScreenshot");
    }
    public void Configure(params PresentationColumn[] value)
    {
        columns=value;widths=value.Select(c=>c.Width).ToArray();minimumWidths=widths.ToArray();manualWidths=false;header.Children.Clear();sortIcons.Clear();
        for(var i=0;i<columns.Length;i++)
        {
            var index=i;var c=columns[i];var cell=new Grid{Width=widths[i]};cell.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});cell.ColumnDefinitions.Add(new(){Width=new(6)});
            var label=InstitutionalVisualTokens.Text(c.Label,12,true);label.TextWrapping=TextWrapping.NoWrap;
            var title=new Grid{ColumnSpacing=4};title.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});title.ColumnDefinitions.Add(new(){Width=GridLength.Auto});title.Children.Add(label);
            var sort=new FontIcon{Glyph="\uE8CB",FontFamily=new("Segoe Fluent Icons"),FontSize=10,Opacity=.65};InstitutionalVisualTokens.Brush(sort,IconElement.ForegroundProperty,"TextSecondary");Grid.SetColumn(sort,1);title.Children.Add(sort);sortIcons.Add(sort);
            var button=new Button{Content=title,Height=32,Padding=new(8,4,8,4),CornerRadius=new(0),BorderThickness=new(0,0,0,1),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch};
            InstitutionalVisualTokens.Brush(button,Control.BackgroundProperty,"SurfaceSecondary");InstitutionalVisualTokens.Brush(button,Control.BorderBrushProperty,"BorderSubtle");
            AutomationProperties.SetName(button,"Ordenar por "+c.Label);ToolTipService.SetToolTip(button,"Ordenar por "+c.Label);button.Click+=(_,_)=>Sort?.Invoke(index);cell.Children.Add(button);
            var thumb=new Thumb{Width=6};InstitutionalVisualTokens.Brush(thumb,Control.BackgroundProperty,"BorderSubtle");
            AutomationProperties.SetName(thumb,"Redimensionar columna "+c.Label);Grid.SetColumn(thumb,1);cell.Children.Add(thumb);
            thumb.DragDelta+=(_,e)=>{manualWidths=true;widths[index]=Math.Clamp(widths[index]+e.HorizontalChange,64,520);cell.Width=widths[index];Template();};header.Children.Add(cell);
        }
        Template();
        FitWidth(Root.ActualWidth);
    }
    private void FitWidth(double available)
    {
        if(manualWidths||minimumWidths.Length==0||available<=0)return;
        var extra=Math.Max(0,available-minimumWidths.Sum()-2);
        for(var i=0;i<widths.Length;i++){widths[i]=minimumWidths[i]+extra*minimumWidths[i]/minimumWidths.Sum();((Grid)header.Children[i]).Width=widths[i];}
        Template();
    }
    public void SortIndicator(int column,bool descending)
    {for(var i=0;i<sortIcons.Count;i++){sortIcons[i].Glyph=i==column?(descending?"\uE70D":"\uE70E"):"\uE8CB";sortIcons[i].Opacity=i==column?1:.65;}}
    private void Template()
    {
        tableRegion.Width=widths.Sum();
        var defs=string.Join("",widths.Select(w=>$"<ColumnDefinition Width=\"{w.ToString(CultureInfo.InvariantCulture)}\"/>"));
        var cells=string.Join("",columns.Select((c,i)=>
        {
            var text=$"<TextBlock Text=\"{{Binding C{i}}}\" FontSize=\"13\" Foreground=\"{{ThemeResource TextPrimary}}\" TextTrimming=\"CharacterEllipsis\" VerticalAlignment=\"Center\" ToolTipService.ToolTip=\"{{Binding C{i}}}\" TextAlignment=\"{(c.Numeric?"Right":"Left")}\"/>";
            if(c.CategoryIndex==i)text=$"<StackPanel Orientation=\"Horizontal\" VerticalAlignment=\"Center\"><Border Width=\"4\" Height=\"20\" Background=\"{{Binding Color}}\" Margin=\"0,0,6,0\"/>{text}</StackPanel>";
            return $"<Border Grid.Column=\"{i}\" Padding=\"8,0\" BorderThickness=\"0,0,1,1\" BorderBrush=\"{{ThemeResource BorderSubtle}}\">{text}</Border>";
        }));
        List.ItemTemplate=(DataTemplate)XamlReader.Load($"<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Grid Height=\"32\"><Grid.ColumnDefinitions>{defs}</Grid.ColumnDefinitions>{cells}</Grid></DataTemplate>");
    }
    public void Rows(IEnumerable<PresentationRow> rows,string? selectedId=null)
    {
        var value=rows.ToArray();List.ItemsSource=value;List.SelectedItem=value.FirstOrDefault(r=>r.Key==selectedId);
        ((FrameworkElement)empty.Tag).Visibility=value.Length==0?Visibility.Visible:Visibility.Collapsed;
    }
}
