using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Synapic.UI.Converters;

/// <summary>
/// Converts boolean to visibility
/// </summary>
public class BooleanToVisibilityConverter : IValueConverter
{
    public bool Inverted { get; set; }
    
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return (Inverted ? !boolValue : boolValue) ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }
    
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            return (Inverted ? visibility != Visibility.Visible : visibility == Visibility.Visible);
        }
        return false;
    }
}

/// <summary>
/// Converts boolean to inverted boolean
/// </summary>
public class BooleanToInvertedBoolConverter : IValueConverter
{
    public bool Inverted { get; set; } = true;
    
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return Inverted ? !boolValue : boolValue;
        }
        return false;
    }
    
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return Inverted ? !boolValue : boolValue;
        }
        return false;
    }
}

/// <summary>
/// Converts integer to boolean (checked state)
/// </summary>
public class IntToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue && parameter != null)
        {
            // Parameter from XAML comes as string, need to parse it
            if (parameter is int pInt)
            {
                return intValue == pInt;
            }
            else if (int.TryParse(parameter.ToString(), out int paramValue))
            {
                return intValue == paramValue;
            }
        }
        return false;
    }
    
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue && boolValue && parameter != null)
        {
            // Parameter from XAML comes as string, need to parse it
            if (parameter is int pInt)
            {
                return pInt;
            }
            else if (int.TryParse(parameter.ToString(), out int paramValue))
            {
                return paramValue;
            }
        }
        return -1;
    }
}