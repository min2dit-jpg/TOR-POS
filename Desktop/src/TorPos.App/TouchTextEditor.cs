using Avalonia.Controls;
namespace TorPos.App;
public static class TouchTextEditor
{
    public static void Insert(TextBox box, string value)
    {
        if(box.IsReadOnly || !box.IsEnabled) return;
        var text=box.Text??"";
        var start=Math.Clamp(Math.Min(box.SelectionStart,box.SelectionEnd),0,text.Length);
        var end=Math.Clamp(Math.Max(box.SelectionStart,box.SelectionEnd),start,text.Length);
        var updated=text[..start]+value+text[end..];
        if(box.MaxLength>0 && updated.Length>box.MaxLength) return;
        box.Text=updated; box.CaretIndex=start+value.Length; box.SelectionStart=box.SelectionEnd=box.CaretIndex;
    }
    public static void Backspace(TextBox box)
    {
        if(box.IsReadOnly || !box.IsEnabled) return;
        if(box.SelectionStart==box.SelectionEnd && box.CaretIndex>0)
        {
            var text=box.Text??"";var end=Math.Min(box.CaretIndex,text.Length);var start=end-1;
            if(start>0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start-1]))start--;
            box.SelectionStart=Math.Max(0,start);box.SelectionEnd=end;
        }
        Insert(box,"");
    }
}
