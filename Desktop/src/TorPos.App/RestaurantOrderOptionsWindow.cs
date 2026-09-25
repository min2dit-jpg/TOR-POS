using Avalonia.Controls;
using Avalonia.Layout;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class RestaurantOrderOptionsWindow : Window
{
    public RestaurantOrderOptionsWindow(Product product,IReadOnlyList<RestaurantOrderOption> options,string recipe)
    {
        var content=new StackPanel { Spacing=8 };
        var checks=options.Where(x=>x.IsActive).Select(x=>new CheckBox { Content=x.Name,Tag=x.Name,MinHeight=38 }).ToArray();
        foreach(var check in checks)content.Children.Add(check);
        content.Children.Add(RestaurantEditorLayout.Label("Bestelloptionen ändern hier keinen Preis. Kostenpflichtige Extras bitte als eigenen Artikel buchen."));
        if(recipe.Length>0)content.Children.Add(new Expander { Header="Zutaten / Rezeptur (Information)",Content=RestaurantEditorLayout.Label(recipe) });
        var apply=RestaurantEditorLayout.Button("ZUR KÜCHE / HINZUFÜGEN");
        apply.Click+=(_,_)=>Close(string.Join(", ",checks.Where(x=>x.IsChecked==true).Select(x=>x.Tag?.ToString())));
        RestaurantEditorLayout.Apply(this,product.Name,content,apply);
    }
}
