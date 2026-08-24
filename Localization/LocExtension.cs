using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace NPC_Plugin_Chooser_2.Localization
{
    /// <summary>
    /// WPF markup extension for localized strings.
    /// Usage: {l:Loc myKey}
    /// </summary>
    public class LocExtension : MarkupExtension
    {
        public string Key { get; set; }

        public LocExtension(string key)
        {
            Key = key;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            // Create a binding to the LocSource singleton's Translation property.
            // When the language changes, LocSource raises a property change notification,
            // and all bindings refresh automatically.
            var binding = new Binding("Translation")
            {
                Source = LocSource.Instance,
                Mode = BindingMode.OneWay,
                Converter = new LocConverter(Key),
            };

            // IMPORTANT: Return binding.ProvideValue(serviceProvider) which returns a
            // BindingExpression, NOT the Binding object itself. Returning the raw Binding
            // causes WPF to crash on properties like Window.Title with:
            // "A 'Binding' cannot be set on the 'Title' property of type 'Window'.
            //  A 'Binding' can only be set on a DependencyProperty of a DependencyObject."
            try
            {
                return binding.ProvideValue(serviceProvider);
            }
            catch
            {
                // Fallback: return the key itself if binding fails
                return Key;
            }
        }
    }

    /// <summary>
    /// Singleton DependencyObject that fires a property change when the language
    /// switches. All LocExtension bindings are bound to this object's Translation
    /// property, so they automatically refresh when the value changes.
    ///
    /// DependencyProperty is used because it is the lowest-level WPF notification
    /// mechanism and works reliably for binding refresh (unlike INotifyPropertyChanged
    /// on a plain object, which some WPF paths ignore).
    /// </summary>
    public class LocSource : DependencyObject, INotifyPropertyChanged
    {
        private static readonly Lazy<LocSource> _instance = new(() => new LocSource());
        public static LocSource Instance => _instance.Value;

        private TranslationService? _translationService;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Subscribe to TranslationService.LanguageChanged to trigger binding refresh.
        /// Must be called AFTER the DI container is built and TranslationService is initialized.
        /// </summary>
        public static void EnsureSubscribed()
        {
            Instance.Subscribe();
        }

        private void Subscribe()
        {
            try
            {
                _translationService = TranslationServiceProvider.GetService();
                if (_translationService != null)
                {
                    _translationService.LanguageChanged += OnLanguageChanged;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocSource] Failed to subscribe: {ex.Message}");
            }
        }

        private void OnLanguageChanged()
        {
            // Fire PropertyChanged for "Translation" — all LocExtension bindings
            // are listening to this property and will re-evaluate via LocConverter.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Translation"));
        }

        /// <summary>
        /// Dummy property that exists only to trigger PropertyChanged.
        /// The actual value is ignored; LocConverter uses the Key it was constructed with.
        /// </summary>
        public string Translation
        {
            get
            {
                try
                {
                    return _translationService?.GetString("__dummy") ?? "";
                }
                catch
                {
                    return "";
                }
            }
        }
    }

    /// <summary>
    /// Converter that takes the Key from its constructor and looks up the
    /// translation from TranslationService each time the binding refreshes.
    /// </summary>
    public class LocConverter : IValueConverter
    {
        private readonly string _key;

        public LocConverter(string key)
        {
            _key = key;
        }

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            try
            {
                var service = TranslationServiceProvider.GetService();
                return service?.GetString(_key) ?? _key;
            }
            catch
            {
                return _key;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Static service locator for TranslationService, used by LocExtension/LocSource/LocConverter
    /// to avoid circular DI dependencies. TranslationService is set once after the container is built.
    /// </summary>
    public static class TranslationServiceProvider
    {
        private static TranslationService? _service;

        public static void SetService(TranslationService service)
        {
            _service = service;
        }

        public static TranslationService? GetService()
        {
            return _service;
        }
    }
}