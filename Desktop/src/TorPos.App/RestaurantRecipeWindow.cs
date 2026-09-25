using Avalonia.Controls;
using Avalonia.Layout;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class RestaurantRecipeWindow : Window
{
    public RestaurantRecipeWindow(RestaurantRecipeRepository repository,Product product)
    {
        var rows=new ListBox { Height=250 };
        var ingredient=new ComboBox { MinHeight=44 };
        var quantity=new NumericUpDown { Minimum=0.001m,Maximum=1000000,Increment=1,Value=1,FormatString="0.###",MinHeight=44 };
        var status=RestaurantEditorLayout.Label("");
        var ingredients=Array.Empty<RestaurantIngredient>(); var recipe=new List<RestaurantRecipeLine>();
        void Refresh()=>rows.ItemsSource=recipe.Select(x=>new Row(x,ingredients.First(i=>i.Id==x.IngredientId))).ToArray();
        var add=RestaurantEditorLayout.Button("ZUTAT ÜBERNEHMEN");
        add.Click+=(_,_)=>
        {
            if(ingredient.SelectedItem is not Choice choice)return;
            recipe.RemoveAll(x=>x.IngredientId==choice.Item.Id);
            recipe.Add(new(choice.Item.Id,quantity.Value??1));Refresh();
        };
        rows.SelectionChanged+=(_,_)=>
        {
            if(rows.SelectedItem is not Row row)return;
            ingredient.SelectedItem=((IEnumerable<Choice>)ingredient.ItemsSource!).FirstOrDefault(x=>x.Item.Id==row.Line.IngredientId);
            quantity.Value=row.Line.Quantity;
        };
        var remove=RestaurantEditorLayout.Button("ZUTAT ENTFERNEN");
        remove.Click+=(_,_)=>{if(rows.SelectedItem is Row row){recipe.Remove(row.Line);Refresh();}};
        var save=RestaurantEditorLayout.Button("REZEPTUR SPEICHERN"); save.Name="EditorSave";
        save.Click+=async (_,_)=>
        {
            save.IsEnabled=false;
            try{await repository.SaveRecipeAsync(product.Id,recipe);Close();}
            catch(Exception ex){status.Text=ex.Message;}
            finally{save.IsEnabled=true;}
        };
        RestaurantEditorLayout.Apply(this,"Rezeptur · "+product.Name,
            new StackPanel { Spacing=8,Children={RestaurantEditorLayout.Label("Mengen pro 1 Artikel. Zutaten werden nicht als Kundenoptionen verkauft."),rows,ingredient,quantity,add,remove,status}},save);
        Opened+=async (_,_)=>
        {
            try
            {
                ingredients=(await repository.ListIngredientsAsync()).ToArray();
                ingredient.ItemsSource=ingredients.Where(x=>x.IsActive).Select(x=>new Choice(x)).ToArray();
                recipe=(await repository.LoadRecipeAsync(product.Id)).ToList();Refresh();
            }
            catch(Exception ex){status.Text=ex.Message;}
        };
    }
    private sealed record Choice(RestaurantIngredient Item){public override string ToString()=>Item.Name+" · "+Item.Unit;}
    private sealed record Row(RestaurantRecipeLine Line,RestaurantIngredient Ingredient){public override string ToString()=>$"{Line.Quantity:0.###} {Ingredient.Unit} {Ingredient.Name}";}
}
