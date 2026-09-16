using Avalonia.Controls;
namespace TorPos.App;
// Compatibility facade. R54 supports German only, including legacy saved preferences.
public static class UiLanguage {
 public static string Current => "DE";
 public static void Set(string? code) { }
 public static string T(string? german) => german ?? "";
 public static void Apply(Control root) { }
}
