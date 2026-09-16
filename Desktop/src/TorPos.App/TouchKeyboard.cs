using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Runtime.CompilerServices;

namespace TorPos.App;

// Input stays in the original TextBox: validation, password masking and scanner handlers remain active.
internal sealed class TouchKeyboard
{
    static readonly ConditionalWeakTable<Window, TouchKeyboard> attached = new();
    static bool installed;
    public static bool AutoOpen { get; set; } = true;
    readonly Window window;
    readonly DockPanel root = new();
    readonly Control body;
    readonly double originalMinHeight;
    ScrollViewer? scrolling;
    readonly StackPanel keys = new() { Spacing = 3, IsVisible = false };
    readonly TextBlock hint = new() { Text = "Eingabefeld auswählen", VerticalAlignment = VerticalAlignment.Center };
    TextBox? target;
    bool upper;
    bool numbers;
    bool symbols;
    public static void Install(bool autoOpen)
    {
        AutoOpen = autoOpen;
        if (installed) return;
        installed = true;
        Control.LoadedEvent.AddClassHandler<Window>((w, args) =>
        {
            if (attached.TryGetValue(w, out _) || w.Content is not Control content) return;
            attached.Add(w, new TouchKeyboard(w, content));
        });
    }
    TouchKeyboard(Window owner, Control content)
    {
        window = owner;
        body = content; originalMinHeight=content.MinHeight;
        var bottom = new StackPanel { Spacing = 3, Margin = new Thickness(4) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        bar.Children.Add(Key("TASTATUR", () => { if(keys.IsVisible) Hide(); else Show(); }));
        bar.Children.Add(hint);
        bottom.Children.Add(bar); bottom.Children.Add(keys);
        DockPanel.SetDock(bottom, Dock.Bottom);
        owner.Content = null;
        root.Children.Add(bottom); root.Children.Add(content); owner.Content = root;
        owner.AddHandler(InputElement.GotFocusEvent, (_, e) =>
        {
            if(e.Source is TextBox box && !box.IsReadOnly && box.IsEnabled)
            {
                target = box;
                hint.Text = box.PasswordChar != '\0' ? "Geschützte Eingabe" : "Bildschirmtastatur · DE";
                var name = ((box.Name ?? "") + " " + (box.PlaceholderText ?? "")).ToLowerInvariant();
                numbers = new[] {"preis", "price", "amount", "quantity", "bestand", "menge", "pin", "cents", "anzahl"}.Any(name.Contains);
                if(keys.IsVisible) Render();
            }
        }, RoutingStrategies.Bubble, true);
        owner.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if(!AutoOpen || e.Pointer.Type != PointerType.Touch) return;
            var box = (e.Source as Control)?.FindAncestorOfType<TextBox>(includeSelf:true);
            if(box is not null && !box.IsReadOnly && box.IsEnabled) { target=box; Show(); }
        }, RoutingStrategies.Bubble, true);
        owner.Deactivated += (_, _) => Hide();
        owner.Closed += (_, _) => { target=null; keys.Children.Clear(); };
    }
    Button Key(string caption, Action action)
    {
        var button = new Button { Content=caption, Focusable=false, MinHeight=36, MinWidth=36,
            Padding=new Thickness(5,2), HorizontalContentAlignment=HorizontalAlignment.Center,
            HorizontalAlignment=HorizontalAlignment.Stretch, FontSize=16 };
        button.Click += (_,_) => { if(window.IsActive) action(); };
        return button;
    }
    bool Valid() => target is { IsReadOnly:false, IsEnabled:true, IsVisible:true } && TopLevel.GetTopLevel(target)==window;
    void Show()
    {
        if(!Valid()) { hint.Text="Zuerst in ein Eingabefeld tippen."; return; }
        if(scrolling is null)
        {
            var previousHeight=body.Bounds.Height;
            root.Children.Remove(body);
            body.MinHeight=Math.Max(originalMinHeight,previousHeight);
            scrolling=new ScrollViewer { Content=body, HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            root.Children.Add(scrolling);
        }
        keys.IsVisible=true; Render();
        Dispatcher.UIThread.Post(() => target?.BringIntoView(), DispatcherPriority.Loaded);
    }
    void Hide()
    {
        keys.IsVisible=false;
        if(scrolling is not null)
        {
            scrolling.Content=null; root.Children.Remove(scrolling); scrolling=null;
            body.MinHeight=originalMinHeight; root.Children.Add(body);
        }
    }
    void Row(IEnumerable<(string Text, Action Action)> entries)
    {
        var list=entries.ToArray(); var row=new Grid { ColumnDefinitions=new ColumnDefinitions(string.Join(",", list.Select(_=>"*"))) };
        for(int i=0;i<list.Length;i++) {var b=Key(list[i].Text,list[i].Action); b.Margin=new Thickness(1); Grid.SetColumn(b,i);row.Children.Add(b);}
        keys.Children.Add(row);
    }
    void Render()
    {
        keys.Children.Clear();
        foreach(var row in symbols ? new[]{"!\"§$%&/()=?", "+*#'_:;<>|", "[]{}\\~^`€"} : numbers ? new[]{"789", "456", "123", "0,-"} : new[]{"1234567890", "qwertzuiopü", "asdfghjklöä", "yxcvbnmß@."})
            Row(row.Select(c => {var text=upper?c.ToString().ToUpperInvariant():c.ToString(); return (text,(Action)(()=>Insert(text)));}));
        Row(new[]{("ABC/123",(Action)(()=>{numbers=!numbers;Render();})),("!?#",()=>{symbols=!symbols;Render();}), ("⇧",()=>{upper=!upper;Render();}), ("Leer",()=>Insert(" ")), ("⌫",Backspace), ("Enter",Enter), ("Schließen",Hide)});
    }
    void Insert(string value)
    {
        if(!Valid()) { Hide(); return; }
        TouchTextEditor.Insert(target!, value);
    }
    void Backspace()
    {
        if(Valid()) TouchTextEditor.Backspace(target!);
    }
    void Enter()
    {
        if(!Valid()) return;
        var box=target!; Hide(); box.Focus();
        box.RaiseEvent(new KeyEventArgs { RoutedEvent=InputElement.KeyDownEvent, Key=Avalonia.Input.Key.Enter, KeyModifiers=KeyModifiers.None });
    }
}
