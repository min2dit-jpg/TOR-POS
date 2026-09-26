using Avalonia.Controls;
using Avalonia.Layout;
using TorPos.Core;

namespace TorPos.App;

public partial class MainWindow
{
    private async Task ShowRestaurantMasterDataAsync(Window owner)
    {
        if(!string.Equals(ProductBuild.FixedEdition,"RESTAURANT",StringComparison.Ordinal) || !_currentUser.Can(UserPermissions.ManageProducts)) return;
        var menu=new Window();var content=new StackPanel { Spacing=6 };
        var status=RestaurantEditorLayout.Label("");
        void Add(string label,Func<Task> action,bool adminOnly=false)
        {
            var button=RestaurantEditorLayout.Button(label);button.IsEnabled=!adminOnly||_currentUser.IsAdmin;
            button.Click+=async (_,_)=>
            {
                button.IsEnabled=false;
                try{await action();}
                catch(Exception ex){status.Text=ex.Message;}
                finally{button.IsEnabled=!adminOnly||_currentUser.IsAdmin;}
            };
            content.Children.Add(button);
        }
        async Task Products(string page)
        {
            var editor=new ProductEditorWindow(_repo,_catalog,_images,_management,_promotions,_currentUser,"",_restaurant.Recipes,page);
            await editor.ShowDialog<bool>(menu);await _catalog.ReloadAsync();BuildCategories();
        }
        async Task Settings(string page)
        {
            await _windowFactory.CreateSettingsWindow(_currentUser,page).ShowDialog<bool>(menu);
            await ReloadSettingsAsync();await RefreshFiscalStatusAsync();
        }
        Add("Artikel",()=>Products("ARTIKEL"));
        Add("Zutaten",()=>new RestaurantIngredientsWindow(_restaurant.Recipes).ShowDialog(menu));
        Add("Warengruppen",()=>Products("WARENGRUPPE"));
        Add("Bestelloptionen",()=>new RestaurantIngredientsWindow(_restaurant.Recipes,options:true).ShowDialog(menu));
        Add("Tische & Bereiche",()=>new RestaurantTableSettingsWindow(_restaurant).ShowDialog(menu));
        Add("Mitarbeiter",()=>Settings("Personal"),true);
        Add("Drucker/Küche",()=>Settings("Geräte"),true);
        Add("Firmendaten",()=>Settings("Firma & Bon"),true);
        content.Children.Add(status);RestaurantEditorLayout.Apply(menu,"Stammdaten",content);
        await menu.ShowDialog(owner);
    }
}
