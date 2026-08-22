using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// True when the first two bound values are equal (<see cref="object.Equals(object, object)"/>,
/// so boxed enums and nulls compare sanely). The generic twin of
/// <see cref="AreModKeysEqualMultiConverter"/> for "is this item the selected one"
/// highlights — e.g. a Mod Issues count chip lighting up when its type is the
/// active issue-type filter.
/// </summary>
public class AreEqualMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not { Length: >= 2 }) return false;
        // UnsetValue means a binding hasn't resolved (e.g. during template load) —
        // never a match.
        if (values[0] == DependencyProperty.UnsetValue || values[1] == DependencyProperty.UnsetValue)
            return false;
        return Equals(values[0], values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => targetTypes.Select(_ => DependencyProperty.UnsetValue).ToArray();
}
