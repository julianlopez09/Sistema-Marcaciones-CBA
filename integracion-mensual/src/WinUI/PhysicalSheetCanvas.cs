using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Monthly.Projects;

namespace SistemaMarcaciones.Desktop.Pilot.WinUI;
/// <summary>Virtual source worksheet with reversible, screen-only layout options.</summary>
internal sealed class PhysicalSheetCanvas
{
    const double RowHeader=40,ColumnHeader=24;
    readonly WorkbookProfile profile;readonly MatrixSheet sheet;readonly SheetProfile physical;readonly string source;
    readonly Func<string,string?> value;readonly IReadOnlyDictionary<string,MatrixCell> cells;readonly Canvas canvas=new();
    readonly WorksheetInteractionIndex index;readonly int frozenRows;
    readonly Func<string,ManualCellPresentation?> presentation;readonly Func<string[]> manualMerges;
    readonly Dictionary<string,FrameworkElement> realized=new(StringComparer.Ordinal);readonly HashSet<string> wanted=new(StringComparer.Ordinal);
    readonly Dictionary<ExcelColor,SolidColorBrush> brushes=[];bool dragging,moved;Point pressedAt;
    string[]? indexedOverlays;CellRectangle[] overlayRectangles=[];
    readonly List<(int R1,int C1,int R2,int C2)> merges=[];readonly Dictionary<string,BitmapImage> pictures=[];
    readonly List<Border> selection=[];readonly HashSet<int> hiddenRows;
    readonly Dictionary<(string Value,int Style,double Width),double> measuredHeights=[];
    readonly double[] xs;double[] ys;double zoom;bool painting,queued,compact,fit,synchronizingScroll;
    public Grid View {get;}=new();
    readonly ScrollBar horizontal=new(){Orientation=Orientation.Horizontal,Height=22,MinHeight=22,IsTabStop=true};
    public ScrollViewer Root {get;}=new(){HorizontalScrollMode=ScrollMode.Enabled,VerticalScrollMode=ScrollMode.Enabled,
        HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden,VerticalScrollBarVisibility=ScrollBarVisibility.Visible,
        ZoomMode=ZoomMode.Disabled,IsTabStop=true,HorizontalContentAlignment=HorizontalAlignment.Left,VerticalContentAlignment=VerticalAlignment.Top};
    public event Action<string,bool,bool>? Selected;
    public event Action<string,Point>? CellClicked;
    public string Coordinate {get;private set;}="A1";public string Anchor {get;private set;}="A1";
    public double MaximumDigitWidth {get;}
    public int RealizedCells {get;private set;}
    public int CollapsedRows {get;private set;}
    public PhysicalSheetCanvas(WorkbookProfile profile,string name,string source,Func<string,string?> value,bool compact=false,bool fit=false,
        Func<string,ManualCellPresentation?>? presentation=null,Func<string[]>? manualMerges=null)
    {
        this.profile=profile;this.source=source;this.value=value;this.compact=compact;this.fit=fit;
        sheet=profile.Draft.Sheets.Single(s=>s.Name==name);physical=profile.Sheets.Single(s=>s.Name==name);
        index=WorksheetInteractionIndex.For(profile,name);cells=index.Cells;frozenRows=index.FrozenHeaderRows;
        this.presentation=presentation??(_=>null);this.manualMerges=manualMerges??(()=>[]);zoom=physical.Zoom/100d;
        var normal=profile.Styles[0];MaximumDigitWidth=Enumerable.Range(0,10).Select(i=>
        {var t=new TextBlock{Text=i.ToString(CultureInfo.InvariantCulture),FontFamily=new(normal.Font),FontSize=normal.Points*96/72};t.Measure(new(double.PositiveInfinity,double.PositiveInfinity));return t.DesiredSize.Width;}).Max();
        var hiddenColumns=physical.HiddenColumns.ToHashSet();hiddenRows=physical.HiddenRows.ToHashSet();
        xs=WorksheetView.Axis(Enumerable.Range(1,physical.MaxColumn).Select(i=>hiddenColumns.Contains(i)?0:WorkbookProfiles.ColumnPixels(sheet.ColumnWidths.GetValueOrDefault(i,physical.DefaultWidth),MaximumDigitWidth)));
        foreach(var merge in sheet.Merges){var parts=merge.Split(':');var a=MatrixCoordinates.Parse(parts[0]);var b=MatrixCoordinates.Parse(parts[^1]);merges.Add((a.Row,a.Column,b.Row,b.Column));}
        ys=[];BuildRows();Root.Content=canvas;
        // Dedicated layout row: never an auto-hiding overlay, and never below the canvas's huge source extent.
        View.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});View.RowDefinitions.Add(new(){Height=GridLength.Auto});
        View.Children.Add(Root);Grid.SetRow(horizontal,1);View.Children.Add(horizontal);
        AutomationProperties.SetName(horizontal,"Desplazamiento horizontal de la hoja Excel");
        horizontal.Scroll+=(_,e)=>{if(!synchronizingScroll)Root.ChangeView(Math.Clamp(e.NewValue,0,Root.ScrollableWidth),null,null,true);};
        Root.ViewChanged+=(_,_)=>{SyncHorizontal();QueuePaint();};Root.SizeChanged+=(_,_)=>{SyncHorizontal();QueuePaint();};
        Root.LayoutUpdated+=(_,_)=>SyncHorizontal();
        Root.Loaded+=async(_,_)=>{await LoadPictures();SyncHorizontal();QueuePaint();};
        // Stable surface. A selection changes only its outline, never the element receiving the gesture.
        canvas.PointerPressed+=(_,e)=>
        {
            var point=e.GetCurrentPoint(Root);if(!point.Properties.IsLeftButtonPressed)return;
            if(Hit(point.Position) is {} coordinate)
            {bool extend=Modifier(Windows.System.VirtualKey.Shift);dragging=true;moved=extend;pressedAt=point.Position;canvas.CapturePointer(e.Pointer);Select(coordinate,extend:extend);e.Handled=true;}
        };
        canvas.PointerMoved+=(_,e)=>
        {
            if(!dragging)return;var point=e.GetCurrentPoint(Root);
            if(Math.Abs(point.Position.X-pressedAt.X)+Math.Abs(point.Position.Y-pressedAt.Y)>5)moved=true;
            if(moved&&Hit(point.Position) is {} coordinate&&Normalize(coordinate)!=Coordinate)Select(coordinate,extend:true);
        };
        // Open options AFTER completing a click, Shift-click or drag range, never during pointer movement.
        canvas.PointerReleased+=(_,e)=>{if(!dragging)return;dragging=false;canvas.ReleasePointerCapture(e.Pointer);CellClicked?.Invoke(Coordinate,e.GetCurrentPoint(Root).Position);e.Handled=true;};
        canvas.PointerCanceled+=(_,_)=>dragging=false;
        canvas.DoubleTapped+=(_,e)=>{if(Hit(e.GetPosition(Root)) is {} coordinate){Select(coordinate,true);e.Handled=true;}};
        canvas.PointerWheelChanged+=(_,e)=>
        {
            var properties=e.GetCurrentPoint(canvas).Properties;
            if(!Modifier(Windows.System.VirtualKey.Shift)&&!properties.IsHorizontalMouseWheel)return;
            ScrollHorizontal(properties.IsHorizontalMouseWheel?properties.MouseWheelDelta:-properties.MouseWheelDelta);e.Handled=true;
        };
        AutomationProperties.SetName(Root,"Hoja Excel "+name+". Flechas para seleccionar; F2 para observación autorizada. Desplazamiento horizontal y vertical.");Size();
    }
    public double Zoom=>zoom;
    void SyncHorizontal()
    {
        if(horizontal.Maximum==Root.ScrollableWidth&&horizontal.ViewportSize==Root.ViewportWidth&&horizontal.Value==Root.HorizontalOffset)return;
        synchronizingScroll=true;try
        {horizontal.Maximum=Root.ScrollableWidth;horizontal.ViewportSize=Root.ViewportWidth;horizontal.LargeChange=Math.Max(1,Root.ViewportWidth*.8);
            horizontal.SmallChange=60;horizontal.Value=Root.HorizontalOffset;horizontal.IsEnabled=Root.ScrollableWidth>0;}
        finally{synchronizingScroll=false;}
    }
    public void ScrollHorizontal(double delta)=>Root.ChangeView(Math.Clamp(Root.HorizontalOffset+delta,0,Root.ScrollableWidth),null,null,true);
    void BuildRows()
    {
        var collapsed=compact?WorksheetView.EmptyTemplateRows(profile,sheet.Name,value).ToHashSet():[];CollapsedRows=collapsed.Count;
        var heights=Enumerable.Range(1,physical.MaxRow).Select(i=>hiddenRows.Contains(i)||collapsed.Contains(i)?0:WorkbookProfiles.RowPixels(sheet.RowHeights.GetValueOrDefault(i,physical.DefaultHeight))).ToArray();
        if(fit)foreach(var cell in sheet.Cells)
        {
            if(heights[cell.Row-1]==0||string.IsNullOrWhiteSpace(value(cell.Coordinate))||Normalize(cell.Coordinate)!=cell.Coordinate)continue;
            var merged=Merge(cell.Coordinate);if(merged.R2!=merged.R1)continue;
            var style=Style(cell);if(style.Rotation!=0)continue;
            double width=Math.Max(1,xs[merged.C2]-xs[merged.C1-1]);var key=(value(cell.Coordinate)??"",style.Id,width);
            if(!measuredHeights.TryGetValue(key,out var height))
            {var text=Text(cell,style,1,true);text.Measure(new(width,double.PositiveInfinity));height=text.DesiredSize.Height+4;measuredHeights[key]=height;}
            heights[cell.Row-1]=Math.Max(heights[cell.Row-1],height);
        }
        ys=WorksheetView.Axis(heights);
    }
    void Size(){canvas.Width=xs[^1]*zoom+RowHeader;canvas.Height=ys[^1]*zoom+ColumnHeader;canvas.Background=Brush(new(null,0,null));}
    public void SetZoom(double requested)
    {
        double ratio=Math.Clamp(requested,.25,2)/zoom;double x=Root.HorizontalOffset*ratio,y=Root.VerticalOffset*ratio;
        zoom*=ratio;InvalidateVisuals();Size();Root.DispatcherQueue.TryEnqueue(()=>{Root.ChangeView(x,y,null,true);QueuePaint();});
    }
    public void Refresh(){InvalidateVisuals();BuildRows();Size();QueuePaint();}
    void InvalidateVisuals(){foreach(var element in realized.Values)canvas.Children.Remove(element);realized.Clear();}
    public void SetView(bool collapseEmpty,bool fitText){compact=collapseEmpty;fit=fitText;Refresh();Reveal(Coordinate);}
    void QueuePaint(){if(queued)return;queued=true;Root.DispatcherQueue.TryEnqueue(()=>{queued=false;Paint();});}
    string Normalize(string coordinate)
    {
        var p=MatrixCoordinates.Parse(coordinate);var overlay=Overlays().FirstOrDefault(m=>m.Contains(p.Row,p.Column));
        return overlay==default?index.Normalize(coordinate):MatrixCoordinates.A1(overlay.R1,overlay.C1);
    }
    (int R1,int C1,int R2,int C2) Merge(string coordinate)
    {
        var p=MatrixCoordinates.Parse(coordinate);var overlay=Overlays().FirstOrDefault(m=>m.Contains(p.Row,p.Column));
        var m=overlay==default?index.Merge(coordinate):overlay;return(m.R1,m.C1,m.R2,m.C2);
    }
    CellRectangle[] Overlays(){var latest=manualMerges();if(!ReferenceEquals(indexedOverlays,latest)){overlayRectangles=latest.Select(CellRectangle.Parse).ToArray();indexedOverlays=latest;}return overlayRectangles;}
    string? Hit(Point point)
    {
        var col=WorksheetView.Hit(xs,point.X-RowHeader,Root.HorizontalOffset,zoom,physical.FrozenColumns);
        var row=WorksheetView.Hit(ys,point.Y-ColumnHeader,Root.VerticalOffset,zoom,frozenRows);
        return col is {} c&&row is {} r?Normalize(MatrixCoordinates.A1(r,c)):null;
    }
    public void Select(string coordinate,bool edit=false,bool extend=false)
    {
        var p=MatrixCoordinates.Parse(coordinate);if(p.Row>physical.MaxRow||p.Column>physical.MaxColumn)return;
        Coordinate=Normalize(coordinate);if(!extend)Anchor=Coordinate;DrawSelection();Root.Focus(FocusState.Pointer);
        AutomationProperties.SetHelpText(Root,$"{sheet.Name}!{Coordinate}. {cells.GetValueOrDefault(Coordinate)?.Role}. {value(Coordinate)}");
        Selected?.Invoke(Coordinate,edit,extend);
    }
    public void Move(int row,int col,bool extend=false)
    {
        var p=MatrixCoordinates.Parse(Coordinate);var m=Merge(Coordinate);
        var r=WorksheetView.NextVisible(ys,row>0?m.R2:p.Row,row);var c=WorksheetView.NextVisible(xs,col>0?m.C2:p.Column,col);
        Select(MatrixCoordinates.A1(r,c),extend:extend);Reveal(Coordinate);
    }
    public void Reveal(string coordinate)
    {
        var m=Merge(coordinate);
        var x=WorksheetView.RevealOffset(xs[m.C1-1]*zoom,xs[m.C2]*zoom,Root.HorizontalOffset,Math.Max(1,Root.ViewportWidth-RowHeader),xs[physical.FrozenColumns]*zoom);
        var y=WorksheetView.RevealOffset(ys[m.R1-1]*zoom,ys[m.R2]*zoom,Root.VerticalOffset,Math.Max(1,Root.ViewportHeight-ColumnHeader),ys[frozenRows]*zoom);
        Root.ChangeView(x,y,null,true);
    }
    public string[] Range()
    {
        return index.Coordinates(index.Selection(Anchor,Coordinate,manualMerges()));
    }
    public string CopyText()
    {
        var a=MatrixCoordinates.Parse(Anchor);var b=MatrixCoordinates.Parse(Coordinate);var lines=new List<string>();
        for(int r=Math.Min(a.Row,b.Row);r<=Math.Max(a.Row,b.Row);r++)lines.Add(string.Join("\t",Enumerable.Range(Math.Min(a.Column,b.Column),Math.Abs(a.Column-b.Column)+1).Select(c=>value(MatrixCoordinates.A1(r,c))??"")));
        return string.Join("\r\n",lines);
    }
    ExcelStyle Style(MatrixCell? cell)=>profile.Styles[int.TryParse(cell?.Style,out var id)?id:0];
    TextBlock Text(MatrixCell? cell,ExcelStyle style,double scale,bool fitText)
    {
        var text=new TextBlock{Text=cell!=null&&cell.Row<=frozenRows&&index.SourceDates.TryGetValue(cell.Coordinate,out var date)?date.ToString("dd/MM/yyyy",CultureInfo.InvariantCulture):WorkbookProfiles.Display(cell==null?null:value(cell.Coordinate),style.NumberFormat,profile.Draft.Date1904),FontFamily=new(style.Font),FontSize=style.Points*96/72*scale,
            FontWeight=style.Bold?Microsoft.UI.Text.FontWeights.Bold:Microsoft.UI.Text.FontWeights.Normal,FontStyle=style.Italic?Windows.UI.Text.FontStyle.Italic:Windows.UI.Text.FontStyle.Normal,
            Foreground=Brush(style.Foreground),TextWrapping=style.Wrap||fitText?TextWrapping.Wrap:TextWrapping.NoWrap,TextTrimming=TextTrimming.None,
            TextAlignment=style.Horizontal switch{"center" or "centerContinuous"=>TextAlignment.Center,"right"=>TextAlignment.Right,"justify"=>TextAlignment.Justify,_=>
                cell?.Type is null or "n"&&double.TryParse(cell?.Value,NumberStyles.Float,CultureInfo.InvariantCulture,out _)?TextAlignment.Right:TextAlignment.Left},
            HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=style.Vertical switch{"top"=>VerticalAlignment.Top,"center"=>VerticalAlignment.Center,_=>VerticalAlignment.Bottom},
            Margin=new((2+style.Indent*3)*scale,0,2*scale,0),IsHitTestVisible=false};
        if(style.Underline)text.TextDecorations=Windows.UI.Text.TextDecorations.Underline;return text;
    }
    // Cell-local AND pane-local clipping prevents scrolled content from covering frozen headers.
    void Fragment(FrameworkElement element,double left,double top,Rect pane)
    {
        var x=Math.Max(left,pane.X);var y=Math.Max(top,pane.Y);var right=Math.Min(left+element.Width,pane.X+pane.Width);var bottom=Math.Min(top+element.Height,pane.Y+pane.Height);
        if(right<=x||bottom<=y){element.Visibility=Visibility.Collapsed;return;}element.Visibility=Visibility.Visible;
        element.Clip=new RectangleGeometry{Rect=new(x-left,y-top,right-x,bottom-y)};Place(element,left+Root.HorizontalOffset,top+Root.VerticalOffset);
    }
    Rect Pane(bool frozenRow,bool frozenCol)
    {
        double splitX=RowHeader+xs[physical.FrozenColumns]*zoom,splitY=ColumnHeader+ys[frozenRows]*zoom;
        double x=frozenCol?RowHeader:splitX,y=frozenRow?ColumnHeader:splitY;
        return new(x,y,Math.Max(0,(frozenCol?splitX:Root.ViewportWidth)-x),Math.Max(0,(frozenRow?splitY:Root.ViewportHeight)-y));
    }
    public void Paint()
    {
        if(painting||Root.ViewportWidth<=0||Root.ViewportHeight<=0)return;painting=true;
        try
        {
            wanted.Clear();RealizedCells=0;
            int fr=frozenRows,fc=physical.FrozenColumns;
            var firstCol=Math.Max(fc+1,WorksheetView.At(xs,(Root.HorizontalOffset+xs[fc]*zoom)/zoom));var lastCol=WorksheetView.At(xs,(Root.HorizontalOffset+Root.ViewportWidth)/zoom);
            var firstRow=Math.Max(fr+1,WorksheetView.At(ys,(Root.VerticalOffset+ys[fr]*zoom)/zoom));var lastRow=WorksheetView.At(ys,(Root.VerticalOffset+Root.ViewportHeight)/zoom);
            var ranges=new[]{(firstRow,lastRow,firstCol,lastCol,false,false),(1,fr,firstCol,lastCol,true,false),
                (firstRow,lastRow,1,fc,false,true),(1,fr,1,fc,true,true)};
            foreach(var range in ranges)
            {
                var drawn=new HashSet<string>();var pane=Pane(range.Item5,range.Item6);
                for(int row=range.Item1;row<=range.Item2;row++)for(int column=range.Item3;column<=range.Item4;column++)
                {
                    if(xs[column]==xs[column-1]||ys[row]==ys[row-1])continue;
                    var coord=Normalize(MatrixCoordinates.A1(row,column));if(!drawn.Add(coord))continue;var merged=Merge(coord);
                    double width=(xs[merged.C2]-xs[merged.C1-1])*zoom,height=(ys[merged.R2]-ys[merged.R1-1])*zoom;if(width<=0||height<=0)continue;
                    var border=Reuse($"CELL:{range.Item5}:{range.Item6}:{coord}",()=>
                    {
                        var cell=cells.GetValueOrDefault(coord);var style=Style(cell);var manual=presentation(coord);
                        if(manual!=null)style=style with{Pattern=manual.Fill==null?"none":"solid",Fill=new(manual.Fill,null,null),Foreground=new(null,1,null)};
                        var text=Text(cell,style,zoom,fit&&style.Rotation==0);
                        var content=new Grid{Width=width,Height=height,IsHitTestVisible=false,Background=style.Pattern=="solid"?Brush(style.Fill):Brush(new(null,0,null)),Clip=new RectangleGeometry{Rect=new(0,0,width,height)}};content.Children.Add(text);
                        if(style.Rotation!=0)text.RenderTransform=new RotateTransform{Angle=style.Rotation==255?90:-style.Rotation,CenterX=width/2,CenterY=height/2};
                        return new Border{Width=width,Height=height,IsHitTestVisible=false,Child=content,BorderThickness=new(Line(style.Left),Line(style.Top),Line(style.Right),Line(style.Bottom)),BorderBrush=Brush(style.Left.Color)};
                    });
                    Fragment(border,RowHeader+xs[merged.C1-1]*zoom-(range.Item6?0:Root.HorizontalOffset),ColumnHeader+ys[merged.R1-1]*zoom-(range.Item5?0:Root.VerticalOffset),pane);RealizedCells++;
                }
                foreach(var pic in physical.Pictures)
                {
                    if(!pictures.TryGetValue(pic.Part,out var image))continue;
                    var width=pic.Width>0?pic.Width:pic.EndColumn is {} ec&&ec<xs.Length?xs[ec-1]+pic.EndOffsetX-xs[pic.Column-1]-pic.OffsetX:0;
                    var height=pic.Height>0?pic.Height:pic.EndRow is {} er&&er<ys.Length?ys[er-1]+pic.EndOffsetY-ys[pic.Row-1]-pic.OffsetY:0;
                    if(width<=0||height<=0)continue;var element=Reuse($"PIC:{range.Item5}:{range.Item6}:{pic.Part}:{pic.Row}:{pic.Column}",()=>new Image{Source=image,Width=width*zoom,Height=height*zoom,Stretch=Stretch.Fill,IsHitTestVisible=false});
                    Fragment(element,RowHeader+(xs[pic.Column-1]+pic.OffsetX)*zoom-(range.Item6?0:Root.HorizontalOffset),ColumnHeader+(ys[pic.Row-1]+pic.OffsetY)*zoom-(range.Item5?0:Root.VerticalOffset),pane);
                }
            }
            foreach(int col in Enumerable.Range(firstCol,Math.Max(0,lastCol-firstCol+1)).Concat(Enumerable.Range(1,fc)))
            {
                var label=Reuse("COL:"+col,()=>Label(MatrixCoordinates.A1(1,col).TrimEnd('1'),(xs[col]-xs[col-1])*zoom,ColumnHeader));
                Fragment(label,RowHeader+xs[col-1]*zoom-(col<=fc?0:Root.HorizontalOffset),0,new(col<=fc?RowHeader:RowHeader+xs[fc]*zoom,0,Math.Max(0,col<=fc?xs[fc]*zoom:Root.ViewportWidth-RowHeader-xs[fc]*zoom),ColumnHeader));
            }
            foreach(int row in Enumerable.Range(firstRow,Math.Max(0,lastRow-firstRow+1)).Concat(Enumerable.Range(1,fr)))
            {
                var label=Reuse("ROW:"+row,()=>Label(row.ToString(CultureInfo.InvariantCulture),RowHeader,(ys[row]-ys[row-1])*zoom));
                Fragment(label,0,ColumnHeader+ys[row-1]*zoom-(row<=fr?0:Root.VerticalOffset),new(0,row<=fr?ColumnHeader:ColumnHeader+ys[fr]*zoom,RowHeader,Math.Max(0,row<=fr?ys[fr]*zoom:Root.ViewportHeight-ColumnHeader-ys[fr]*zoom)));
            }
            Fragment(Reuse("CORNER",()=>Label("",RowHeader,ColumnHeader)),0,0,new(0,0,RowHeader,ColumnHeader));
            foreach(var key in realized.Keys.Where(k=>!wanted.Contains(k)).ToArray()){canvas.Children.Remove(realized[key]);realized.Remove(key);}DrawSelection();
        }finally{painting=false;}
    }
    Border Label(string text,double width,double height)=>new(){Width=width,Height=height,IsHitTestVisible=false,Background=Brush(new(null,null,22)),Child=new TextBlock{Text=text,TextAlignment=TextAlignment.Center,Foreground=Brush(new(null,1,null)),FontSize=11}};
    void DrawSelection()
    {
        foreach(var element in selection)canvas.Children.Remove(element);selection.Clear();var m=index.Selection(Anchor,Coordinate,manualMerges());
        foreach(bool frozenRow in new[]{false,true})foreach(bool frozenCol in new[]{false,true})
        {
            var border=new Border{Width=(xs[m.C2]-xs[m.C1-1])*zoom,Height=(ys[m.R2]-ys[m.R1-1])*zoom,BorderThickness=new(2),IsHitTestVisible=false};
            InstitutionalVisualTokens.Brush(border,Border.BorderBrushProperty,"InstitutionalRed");
            Fragment(border,RowHeader+xs[m.C1-1]*zoom-(frozenCol?0:Root.HorizontalOffset),ColumnHeader+ys[m.R1-1]*zoom-(frozenRow?0:Root.VerticalOffset),Pane(frozenRow,frozenCol));selection.Add(border);
        }
    }
    double Line(ExcelBorder b)=>b.Line switch{null=>.3,"hair"=>.4*zoom,"medium" or "mediumDashed"=>2*zoom,"thick" or "double"=>3*zoom,_=>zoom};
    FrameworkElement Reuse(string key,Func<FrameworkElement> factory){wanted.Add(key);if(!realized.TryGetValue(key,out var element)){element=factory();realized.Add(key,element);}return element;}
    void Place(FrameworkElement element,double left,double top){Canvas.SetLeft(element,left);Canvas.SetTop(element,top);if(element.Parent!=canvas)canvas.Children.Add(element);}
    public static bool Modifier(Windows.System.VirtualKey key)=>(Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)&Windows.UI.Core.CoreVirtualKeyStates.Down)!=0;
    SolidColorBrush Brush(ExcelColor color)
    {
        if(brushes.TryGetValue(color,out var existing))return existing;
        string rgb=color.Rgb??(color.Theme is {} t&&t<profile.ThemeColors.Length?profile.ThemeColors[t]:color.Theme==0?profile.IndexedColors[1]:null)??
            (color.Indexed is {} indexed&&indexed<profile.IndexedColors.Length?profile.IndexedColors[indexed]:null)??
            (profile.ThemeColors.Length>1?profile.ThemeColors[1]:"000000");
        if(rgb.Length==8)rgb=rgb[2..];if(rgb.Length!=6)rgb="000000";
        byte Component(int offset){double v=Convert.ToByte(rgb.Substring(offset,2),16);return (byte)Math.Clamp(Math.Round(color.Tint<0?v*(1+color.Tint):v+(255-v)*color.Tint),0,255);}
        var brush=new SolidColorBrush(Color.FromArgb(255,Component(0),Component(2),Component(4)));brushes.Add(color,brush);return brush;
    }
    // A retained canvas is detached/re-attached after canonical commits and working-row changes.
    // Loaded can therefore fire again, including while the first asynchronous bitmap load awaits.
    // One task per immutable source canvas prevents duplicate dictionary insertion; failures remain visible.
    Task? pictureLoad;
    Task LoadPictures()=>pictureLoad??=LoadPicturesOnce();
    async Task LoadPicturesOnce()
    {
        using var f=File.OpenRead(source);using var z=new ZipArchive(f);
        foreach(var picture in physical.Pictures.DistinctBy(p=>p.Part))
        {using var input=z.GetEntry(picture.Part)!.Open();using var memory=new MemoryStream();await input.CopyToAsync(memory);memory.Position=0;var image=new BitmapImage();await image.SetSourceAsync(memory.AsRandomAccessStream());pictures.Add(picture.Part,image);}
    }
}
