using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TorPos.App;

internal static class RestaurantEditorLayout
{
    public static Button Button(string text) => new() { Content=text,MinHeight=44,Margin=new Thickness(4) };
    public static TextBlock Label(string text) => new() { Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,4) };
    public static void Apply(Window window,string title,Control content,params Control[] actions)
    {
        window.Title="TOR Restaurant · "+title; window.Width=1000; window.Height=680;
        window.MinWidth=720; window.MinHeight=480; window.WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var close=Button("SCHLIESSEN"); close.Name="EditorClose"; close.Click+=(_,_)=>window.Close();
        var footer=new WrapPanel { HorizontalAlignment=HorizontalAlignment.Right };
        foreach(var action in actions) footer.Children.Add(action);
        footer.Children.Add(close);
        var root=new Grid { Margin=new Thickness(18),RowDefinitions=new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(new TextBlock { Text=title,FontSize=24,FontWeight=FontWeight.Bold,Margin=new Thickness(0,0,0,12) });
        var scroll=new ScrollViewer { Content=content,VerticalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(scroll,1); root.Children.Add(scroll); Grid.SetRow(footer,2); root.Children.Add(footer); window.Content=root;
    }
}
