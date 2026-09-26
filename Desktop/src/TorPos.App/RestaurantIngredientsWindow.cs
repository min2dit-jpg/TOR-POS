using Avalonia.Controls;
using Avalonia.Layout;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class RestaurantIngredientsWindow : Window
{
    public RestaurantIngredientsWindow(RestaurantRecipeRepository repository,bool options=false)
    {
        var list=new ListBox { Height=230 };
        var name=new TextBox { MaxLength=options?100:120,MinHeight=44 };
        var unit=new ComboBox { ItemsSource=new[]{"g","ml","Stück"},SelectedIndex=0,MinHeight=44 };
        var active=new CheckBox { Content="Aktiv",IsChecked=true };
        var status=RestaurantEditorLayout.Label("");
        long id=0;
        async Task Reload()
        {
            list.ItemsSource=options
                ? (await repository.ListOptionsAsync()).Select(x=>new Entry(x.Id,x.Name,"",x.IsActive)).ToArray()
                : (await repository.ListIngredientsAsync()).Select(x=>new Entry(x.Id,x.Name,x.Unit,x.IsActive)).ToArray();
        }
        list.SelectionChanged+=(_,_)=>
        {
            if(list.SelectedItem is not Entry item)return;
            id=item.Id; name.Text=item.Name; unit.SelectedItem=item.Unit; active.IsChecked=item.Active;
        };
        var fresh=RestaurantEditorLayout.Button("NEU");
        fresh.Click+=(_,_)=> { id=0;list.SelectedItem=null;name.Text="";unit.SelectedIndex=0;active.IsChecked=true;status.Text=""; };
        var save=RestaurantEditorLayout.Button("SPEICHERN"); save.Name="EditorSave";
        save.Click+=async (_,_)=>
        {
            save.IsEnabled=false;
            try
            {
                if(options) await repository.SaveOptionAsync(new(id,name.Text??"",active.IsChecked==true));
                else id=await repository.SaveIngredientAsync(new(id,name.Text??"",unit.SelectedItem?.ToString()??"g",active.IsChecked==true));
                await Reload(); status.Text="Gespeichert.";
                // New options must be selected after creation before saving again.
                if(options) {id=0;name.Text="";}
            }
            catch(Exception ex){status.Text=ex.Message;}
            finally{save.IsEnabled=true;}
        };
        var content=new StackPanel { Spacing=6,Children={list,RestaurantEditorLayout.Label("Name"),name} };
        if(!options) {content.Children.Add(RestaurantEditorLayout.Label("Basiseinheit pro Rezepturmenge"));content.Children.Add(unit);}
        content.Children.Add(active);
        content.Children.Add(RestaurantEditorLayout.Label(options
            ? "Kundenwünsche ohne Preisänderung, z. B. ohne Zwiebeln. Kostenpflichtige Extras als eigenen Artikel buchen."
            : "Rezepturverbrauch pro Artikel, z. B. 180 g Fleisch. Kundenwünsche werden separat unter Bestelloptionen gepflegt."));
        content.Children.Add(status);
        RestaurantEditorLayout.Apply(this,options?"Bestelloptionen":"Zutaten",content,fresh,save);
        Opened+=async (_,_)=> {try{await Reload();}catch(Exception ex){status.Text=ex.Message;}};
    }
    private sealed record Entry(long Id,string Name,string Unit,bool Active)
    { public override string ToString()=>Name+(Unit.Length>0?" · "+Unit:"")+(Active?"":" · inaktiv"); }
}
