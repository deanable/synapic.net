using System.Windows;
using System.Windows.Controls;

namespace Synapic.UI.Helpers;

/// <summary>
/// Helper for PasswordBox binding
/// </summary>
public static class PasswordBoxHelper
{
    public static readonly DependencyProperty PasswordProperty =
        DependencyProperty.RegisterAttached("Password",
            typeof(string), typeof(PasswordBoxHelper),
            new FrameworkPropertyMetadata(string.Empty, OnPasswordPropertyChanged));

    private static readonly DependencyProperty PasswordBindingProperty =
        DependencyProperty.RegisterAttached("PasswordBinding",
            typeof(bool), typeof(PasswordBoxHelper), new PropertyMetadata(false, OnPasswordBindingPropertyChanged));

    public static string GetPassword(DependencyObject dp)
    {
        return (string)dp.GetValue(PasswordProperty);
    }

    public static void SetPassword(DependencyObject dp, string value)
    {
        dp.SetValue(PasswordProperty, value);
    }

    public static bool GetPasswordBinding(DependencyObject dp)
    {
        return (bool)dp.GetValue(PasswordBindingProperty);
    }

    public static void SetPasswordBinding(DependencyObject dp, bool value)
    {
        dp.SetValue(PasswordBindingProperty, value);
    }

    private static void OnPasswordPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            if ((bool)passwordBox.GetValue(PasswordBindingProperty))
            {
                return;
            }

            if ((string)e.NewValue != passwordBox.Password)
            {
                passwordBox.Password = (string)e.NewValue;
            }
        }
    }

    private static void OnPasswordBindingPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            if ((bool)e.OldValue)
            {
                passwordBox.PasswordChanged -= PasswordChanged;
            }
            if ((bool)e.NewValue)
            {
                passwordBox.PasswordChanged += PasswordChanged;
            }
        }
    }

    private static void PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            SetPassword(passwordBox, passwordBox.Password);
        }
    }
}