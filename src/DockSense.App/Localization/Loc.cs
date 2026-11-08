using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;

namespace DockSense.App.Localization;

/// <summary>
/// UI strings with live language switching. XAML binds through <see cref="TExtension"/>; code calls
/// <see cref="T"/> / <see cref="F"/>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string SystemLanguage = "system";
    public const string English = "en";
    public const string SimplifiedChinese = "zh-Hans";

    private static readonly ResourceManager Resources = new("DockSense.App.Localization.Strings", typeof(Loc).Assembly);

    private Loc()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static Loc Instance { get; } = new();

    public CultureInfo Culture { get; private set; } = Resolve(SystemLanguage);

    public string this[string key] => Resources.GetString(key, Culture) ?? key;

    public static string T(string key) => Instance[key];

    public static string F(string key, params object?[] args) => string.Format(Instance.Culture, Instance[key], args);

    /// <summary>"system", "en" or "zh-Hans". Raises change notifications so every bound string refreshes.</summary>
    public void SetLanguage(string language)
    {
        Culture = Resolve(language);
        CultureInfo.CurrentUICulture = Culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
    }

    private static CultureInfo Resolve(string language)
    {
        if (language is English or SimplifiedChinese)
        {
            return CultureInfo.GetCultureInfo(language);
        }

        // Follow Windows: any Chinese UI language except Traditional maps to Simplified Chinese, the rest to English.
        var system = CultureInfo.InstalledUICulture;
        var isSimplifiedChinese = system.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            && !system.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
            && system.Name is not ("zh-TW" or "zh-HK" or "zh-MO");
        return CultureInfo.GetCultureInfo(isSimplifiedChinese ? SimplifiedChinese : English);
    }
}

/// <summary>XAML: <c>Text="{loc:T Nav_Status}"</c>. Updates when the language changes.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
