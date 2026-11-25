using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;
namespace SistemaMarcaciones.Desktop.Pilot.WinUI;

// Presentation palette, provisional reference to protected CBA assets; not a graphic-manual claim.
// Labour novelty RGBs are obtained only from SharedNoveltyCatalog (F2), never from these tokens.
internal static class InstitutionalVisualTokens
{
    public const string DictionaryXaml="""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <ResourceDictionary.ThemeDictionaries>
            <ResourceDictionary x:Key="Light">
              <SolidColorBrush x:Key="SurfacePrimary" Color="#F4F6F8"/><SolidColorBrush x:Key="SurfaceSecondary" Color="#EDF1F5"/>
              <SolidColorBrush x:Key="SurfaceElevated" Color="#FFFFFF"/><SolidColorBrush x:Key="BorderSubtle" Color="#DCE3EB"/>
              <SolidColorBrush x:Key="TextPrimary" Color="#14253B"/><SolidColorBrush x:Key="TextSecondary" Color="#52657C"/>
              <SolidColorBrush x:Key="TextMuted" Color="#63758A"/><SolidColorBrush x:Key="Selection" Color="#FCE9ED"/>
              <SolidColorBrush x:Key="Hover" Color="#E7EDF4"/><SolidColorBrush x:Key="TableAlternate" Color="#F9FAFC"/>
            </ResourceDictionary>
            <ResourceDictionary x:Key="Dark">
              <SolidColorBrush x:Key="SurfacePrimary" Color="#111820"/><SolidColorBrush x:Key="SurfaceSecondary" Color="#303946"/>
              <SolidColorBrush x:Key="SurfaceElevated" Color="#1B222B"/><SolidColorBrush x:Key="BorderSubtle" Color="#414B58"/>
              <SolidColorBrush x:Key="TextPrimary" Color="#F4F7FB"/><SolidColorBrush x:Key="TextSecondary" Color="#C1CDD9"/>
              <SolidColorBrush x:Key="TextMuted" Color="#ADBFD2"/><SolidColorBrush x:Key="Selection" Color="#57313D"/>
              <SolidColorBrush x:Key="Hover" Color="#2D3845"/><SolidColorBrush x:Key="TableAlternate" Color="#1E2732"/>
            </ResourceDictionary>
          </ResourceDictionary.ThemeDictionaries>
          <SolidColorBrush x:Key="InstitutionalNavy" Color="#071D35"/><SolidColorBrush x:Key="InstitutionalRed" Color="#E31E2D"/>
          <SolidColorBrush x:Key="InstitutionalBlack" Color="#101B28"/><SolidColorBrush x:Key="InstitutionalWhite" Color="#FFFFFF"/>
          <SolidColorBrush x:Key="InstitutionalGold" Color="#C8A660"/><SolidColorBrush x:Key="SidebarMuted" Color="#B5C6DA"/>
          <SolidColorBrush x:Key="SidebarHover" Color="#183450"/>
          <Style TargetType="TextBlock"><Setter Property="FontFamily" Value="Segoe UI Variable"/><Setter Property="FontSize" Value="13"/>
            <Setter Property="Foreground" Value="{ThemeResource TextPrimary}"/></Style>
          <Style TargetType="Button"><Setter Property="FontFamily" Value="Segoe UI Variable"/><Setter Property="FontSize" Value="13"/>
            <Setter Property="MinHeight" Value="32"/><Setter Property="Padding" Value="12,4"/><Setter Property="CornerRadius" Value="4"/>
            <Setter Property="Background" Value="{ThemeResource SurfaceElevated}"/><Setter Property="Foreground" Value="{ThemeResource TextPrimary}"/>
            <Setter Property="BorderBrush" Value="{ThemeResource BorderSubtle}"/><Setter Property="BorderThickness" Value="1"/></Style>
          <Style x:Key="InstitutionalPrimaryButton" TargetType="Button"><Setter Property="FontFamily" Value="Segoe UI Variable"/>
            <Setter Property="MinHeight" Value="32"/><Setter Property="FontSize" Value="13"/><Setter Property="Padding" Value="16,4"/>
            <Setter Property="CornerRadius" Value="4"/><Setter Property="Background" Value="{ThemeResource InstitutionalRed}"/>
            <Setter Property="Foreground" Value="{ThemeResource InstitutionalWhite}"/><Setter Property="BorderThickness" Value="0"/></Style>
          <Style x:Key="InstitutionalSidebarButton" TargetType="Button">
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="HorizontalAlignment" Value="Stretch"/>
            <Setter Property="Padding" Value="12,8"/><Setter Property="MinHeight" Value="40"/><Setter Property="CornerRadius" Value="4"/>
            <Setter Property="Background" Value="{ThemeResource InstitutionalNavy}"/><Setter Property="Foreground" Value="{ThemeResource InstitutionalWhite}"/>
            <Setter Property="UseSystemFocusVisuals" Value="True"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
              <Border x:Name="ButtonSurface" Background="{TemplateBinding Background}" CornerRadius="4" Padding="{TemplateBinding Padding}">
                <VisualStateManager.VisualStateGroups><VisualStateGroup x:Name="CommonStates">
                  <VisualState x:Name="Normal"/><VisualState x:Name="PointerOver"><VisualState.Setters><Setter Target="ButtonSurface.Opacity" Value="0.82"/></VisualState.Setters></VisualState>
                  <VisualState x:Name="Pressed"><VisualState.Setters><Setter Target="ButtonSurface.Opacity" Value="0.68"/></VisualState.Setters></VisualState>
                  <VisualState x:Name="Disabled"><VisualState.Setters><Setter Target="ButtonSurface.Opacity" Value="0.45"/></VisualState.Setters></VisualState>
                </VisualStateGroup></VisualStateManager.VisualStateGroups>
                <ContentPresenter Content="{TemplateBinding Content}" HorizontalAlignment="Stretch" VerticalAlignment="Center"/>
              </Border>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="InstitutionalRow" TargetType="ListViewItem"><Setter Property="MinHeight" Value="32"/>
            <Setter Property="Padding" Value="0"/><Setter Property="Margin" Value="0"/><Setter Property="CornerRadius" Value="0"/>
            <Setter Property="UseSystemFocusVisuals" Value="True"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="Background" Value="{ThemeResource SurfaceElevated}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListViewItem">
              <ListViewItemPresenter Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}"
                HorizontalContentAlignment="{TemplateBinding HorizontalContentAlignment}" ContentMargin="{TemplateBinding Padding}"
                Background="{TemplateBinding Background}" PointerOverBackground="{ThemeResource Hover}"
                SelectedBackground="{ThemeResource Selection}" SelectedPointerOverBackground="{ThemeResource Selection}"
                SelectedPressedBackground="{ThemeResource Selection}">
                <VisualStateManager.VisualStateGroups><VisualStateGroup x:Name="CommonStates">
                  <VisualState x:Name="Normal"/><VisualState x:Name="PointerOver"/><VisualState x:Name="Pressed"/>
                  <VisualState x:Name="Selected"/><VisualState x:Name="PointerOverSelected"/>
                  <VisualState x:Name="PressedSelected"/><VisualState x:Name="Disabled"/>
                </VisualStateGroup></VisualStateManager.VisualStateGroups>
              </ListViewItemPresenter>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="InstitutionalRowAlternate" TargetType="ListViewItem" BasedOn="{StaticResource InstitutionalRow}">
            <Setter Property="Background" Value="{ThemeResource TableAlternate}"/>
          </Style>
        </ResourceDictionary>
        """;
    public static void Install()=>Microsoft.UI.Xaml.Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(DictionaryXaml));
    public static void Brush(DependencyObject target,DependencyProperty property,string token)
    {
        // The framework keeps ThemeResource expressions live across RequestedTheme changes.
        var resource=(Border)XamlReader.Load($"<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{{ThemeResource {token}}}\"/>");
        // For newly created controls use a binding to this live themed resource carrier.
        if(target is FrameworkElement element)
        {
            element.SetBinding(property,new Microsoft.UI.Xaml.Data.Binding{Source=resource,Path=new PropertyPath("Background"),Mode=Microsoft.UI.Xaml.Data.BindingMode.OneWay});
            // A detached carrier must follow its actual target, not the OS theme by accident.
            void Apply()=>resource.RequestedTheme=element.ActualTheme;
            element.Loaded+=(_,_)=>Apply();element.ActualThemeChanged+=(_,_)=>Apply();Apply();
        }
    }
    public static TextBlock Text(string text,int size=13,bool semibold=false,string token="TextPrimary")
    {
        var value=(TextBlock)XamlReader.Load($"<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Foreground=\"{{ThemeResource {token}}}\"/>");
        value.Text=text;value.FontFamily=new("Segoe UI Variable");value.FontSize=size;
        value.FontWeight=new FontWeight{Weight=(ushort)(semibold?600:400)};value.TextWrapping=TextWrapping.Wrap;return value;
    }
    public static Border Surface(UIElement child,string token="SurfaceElevated",int padding=12)
    {
        var border=(Border)XamlReader.Load($"<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{{ThemeResource {token}}}\" BorderBrush=\"{{ThemeResource BorderSubtle}}\"/>");
        border.Child=child;border.Padding=new(padding);border.CornerRadius=new(4);border.BorderThickness=new(1);return border;
    }
    public static Border Badge(string text,bool attention=false)
    {
        var label=Text(text,11,true);label.TextWrapping=TextWrapping.NoWrap;
        var value=Surface(label,attention?"Selection":"SurfaceSecondary",6);
        value.Padding=new(8,3,8,3);value.CornerRadius=new(3);value.VerticalAlignment=VerticalAlignment.Center;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(value,text);return value;
    }
}
