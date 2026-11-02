using System;
using System.Globalization;
using System.Windows.Data;
using SkyBar.SystemIntegration;

namespace SkyBar.UI.DesignSystem;

/// <summary>
/// Mengubah path file menjadi ikon/thumbnail Shell resolusi tinggi.
/// ConverterParameter = ukuran piksel yang diminta (default 64).
/// </summary>
public sealed class ShellIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path) return null;
        int size = parameter is string s && int.TryParse(s, out int parsed) ? parsed : 64;
        return ShellIconLoader.GetThumbnail(path, size);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

