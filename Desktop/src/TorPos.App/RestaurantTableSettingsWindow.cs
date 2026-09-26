using Avalonia.Controls;
using Avalonia.Layout;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class RestaurantTableSettingsWindow : Window
{
    public RestaurantTableSettingsWindow(RestaurantRepository repository)
    {
        var areas=new ListBox { Height=170 }; var tables=new ListBox { Height=170 };
        var areaName=new TextBox { MaxLength=80,MinHeight=44 };
        var areaActive=new CheckBox { Content="Bereich verwenden",IsChecked=true };
        var tableArea=new ComboBox { MinHeight=44 }; var code=new TextBox { MaxLength=40,MinHeight=44 };
        var name=new TextBox { MaxLength=80,MinHeight=44 };
        var seats=new NumericUpDown { Minimum=1,Maximum=99,Value=4,MinHeight=44 };
        var tableActive=new CheckBox { Content="Tisch verwenden",IsChecked=true };
        var status=RestaurantEditorLayout.Label("");
        RestaurantArea? area=null; RestaurantTable? table=null;
        async Task Reload()
        {
            var choices=(await repository.ListAreasAsync()).Select(a=>new AreaChoice(a)).ToArray();
            areas.ItemsSource=choices;tableArea.ItemsSource=choices;
            tables.ItemsSource=(await repository.ListAllTablesAsync()).Select(t=>new TableChoice(t)).ToArray();
        }
        areas.SelectionChanged+=(_,_)=>{if(areas.SelectedItem is AreaChoice a){area=a.Item;areaName.Text=area.Name;areaActive.IsChecked=area.IsActive;}};
        tables.SelectionChanged+=(_,_)=>
        {
            if(tables.SelectedItem is not TableChoice t)return;
            table=t.Item;code.Text=table.Code;name.Text=table.DisplayName;seats.Value=table.Seats;tableActive.IsChecked=table.IsActive;
            tableArea.SelectedItem=((IEnumerable<AreaChoice>)tableArea.ItemsSource!).FirstOrDefault(x=>x.Item.Id==table.AreaId);
        };
        var tabs=new TabControl();
        tabs.Items.Add(new TabItem { Header="Tische",Content=new StackPanel { Spacing=6,Children={tables,RestaurantEditorLayout.Label("Bereich"),tableArea,RestaurantEditorLayout.Label("Tischcode"),code,RestaurantEditorLayout.Label("Tischname"),name,RestaurantEditorLayout.Label("Plätze"),seats,tableActive} } });
        tabs.Items.Add(new TabItem { Header="Bereiche",Content=new StackPanel { Spacing=6,Children={areas,RestaurantEditorLayout.Label("Name (Gastraum, Terrasse, Außenbereich …)"),areaName,areaActive} } });
        tabs.SelectedIndex=0;
        var fresh=RestaurantEditorLayout.Button("NEU");
        fresh.Click+=(_,_)=>
        {
            if(tabs.SelectedIndex==0){table=null;tables.SelectedItem=null;code.Text="";name.Text="";seats.Value=4;tableActive.IsChecked=true;}
            else{area=null;areas.SelectedItem=null;areaName.Text="";areaActive.IsChecked=true;}
        };
        var save=RestaurantEditorLayout.Button("SPEICHERN");save.Name="EditorSave";
        save.Click+=async (_,_)=>
        {
            save.IsEnabled=false;
            try
            {
                if(tabs.SelectedIndex==0)
                {
                    if(tableArea.SelectedItem is not AreaChoice choice)throw new InvalidOperationException("Bereich auswählen.");
                    await repository.SaveTableSettingsAsync(new(table?.Id??0,choice.Item.Id,code.Text??"",name.Text??"",(int)(seats.Value??4),table?.SortOrder??0,tableActive.IsChecked==true,table?.Version??0));
                    table=null;code.Text="";name.Text="";
                }
                else {await repository.SaveAreaSettingsAsync(area?.Id??0,areaName.Text??"",areaActive.IsChecked==true);area=null;areaName.Text="";}
                await Reload();status.Text="Gespeichert.";
            }
            catch(Exception ex){status.Text=ex.Message;}
            finally{save.IsEnabled=true;}
        };
        RestaurantEditorLayout.Apply(this,"Tische & Bereiche",new StackPanel { Children={tabs,status}},fresh,save);
        Opened+=async (_,_)=>{try{await Reload();}catch(Exception ex){status.Text=ex.Message;}};
    }
    private sealed record AreaChoice(RestaurantArea Item){public override string ToString()=>Item.Name+(Item.IsActive?"":" · inaktiv");}
    private sealed record TableChoice(RestaurantTable Item){public override string ToString()=>Item.DisplayName+" · "+Item.Code+(Item.IsActive?"":" · inaktiv");}
}
