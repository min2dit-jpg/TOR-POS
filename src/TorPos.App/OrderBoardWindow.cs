using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TorPos.Infrastructure;
namespace TorPos.App;

public sealed class OrderBoardWindow : Window
{
 readonly OrderWorkflowService service;readonly bool training;readonly string actor;
 readonly ListBox list=new();readonly TextBox search=new(){PlaceholderText="Abholnummer / Parknummer suchen"};
 readonly TextBox note=new(){PlaceholderText="Bestellhinweis (Deutsch)",MaxLength=300};readonly TextBlock status=new(){TextWrapping=Avalonia.Media.TextWrapping.Wrap};
 readonly CheckBox all=new(){Content="Ausgegebene Bestellungen anzeigen"};
 IReadOnlyList<OrderOverview> rows=Array.Empty<OrderOverview>();bool busy;
 public OrderBoardWindow(OrderWorkflowService service,bool training,string actor)
 {
  this.service=service;this.training=training;this.actor=actor;
  Title="Bestellübersicht";Width=1050;Height=680;MinWidth=780;MinHeight=500;WindowStartupLocation=WindowStartupLocation.CenterOwner;
  var root=new Grid{Margin=new Thickness(18),RowDefinitions=new("Auto,Auto,*,Auto,Auto,Auto"),RowSpacing=12};Content=root;
  root.Children.Add(new TextBlock{Text="BESTELLÜBERSICHT"+(training?" · TRAINING":""),FontSize=26});
  var filter=new WrapPanel{Orientation=Orientation.Horizontal};search.Width=320;filter.Children.Add(search);filter.Children.Add(all);filter.Children.Add(Button("AKTUALISIEREN",Reload));Grid.SetRow(filter,1);root.Children.Add(filter);
  Grid.SetRow(list,2);root.Children.Add(list);Grid.SetRow(note,3);root.Children.Add(note);
  var actions=new WrapPanel{Orientation=Orientation.Horizontal};
  foreach(var (label,state) in new[]{("IN VORBEREITUNG","PREPARING"),("ABHOLBEREIT","READY"),("AUSGEGEBEN","DELIVERED")})actions.Children.Add(Button(label,()=>Save(state)));
  foreach(var text in new[]{"Ohne Zwiebeln","Extra scharf","Zum Mitnehmen"})actions.Children.Add(Button(text,()=>{note.Text=string.IsNullOrWhiteSpace(note.Text)?text:note.Text+"; "+text;return Task.CompletedTask;}));
  actions.Children.Add(Button("HINWEIS SPEICHERN",()=>Save((list.SelectedItem as OrderOverview)?.State??"")));
  actions.Children.Add(Button("ZUM KASSIEREN / BEARBEITEN",()=>{if(list.SelectedItem is OrderOverview row&&row.Open)Close((long?)row.Id);else status.Text="Nur unbezahlte Bestellungen können aufgerufen werden.";return Task.CompletedTask;}));
  actions.Children.Add(Button("SCHLIESSEN",()=>{Close((long?)null);return Task.CompletedTask;}));
  Grid.SetRow(actions,4);root.Children.Add(actions);Grid.SetRow(status,5);root.Children.Add(status);
  status.Text="Status ändern erzeugt keine Zahlung. Hinweise werden beim nächsten Küchenbon mitgedruckt. Maximal 500 Bestellungen.";
  list.SelectionChanged+=(_,_)=>note.Text=(list.SelectedItem as OrderOverview)?.Note??"";
  search.TextChanged+=(_,_)=>Filter();all.IsCheckedChanged+=async(_,_)=>await Reload();Opened+=async(_,_)=>await Reload();
 }
 Button Button(string label,Func<Task> action){var b=new Button{Content=label,Margin=new Thickness(3),MinHeight=42};b.Click+=async(_,_)=>{if(busy)return;busy=true;try{await action();}catch(Exception ex){CrashLog.WriteException("Order board",ex);status.Text=ex.Message;}finally{busy=false;}};return b;}
 async Task Reload(){try{rows=await service.ListAsync(training,all.IsChecked==true);Filter();}catch(Exception ex){status.Text="Laden fehlgeschlagen: "+ex.Message;}}
 void Filter(){var term=search.Text?.Trim()??"";list.ItemsSource=rows.Where(x=>term.Length==0||x.PickupNumber.ToString("000").Contains(term)||$"P{x.ParkNumber:000000}".Contains(term,StringComparison.OrdinalIgnoreCase)).ToArray();}
 async Task Save(string state){if(list.SelectedItem is not OrderOverview row){status.Text="Bitte Bestellung auswählen.";return;}await service.UpdateAsync(row.Id,row.Version,state,note.Text??"",actor,training);await Reload();status.Text="Gespeichert. Zahlungsstatus bleibt unverändert.";}
}
