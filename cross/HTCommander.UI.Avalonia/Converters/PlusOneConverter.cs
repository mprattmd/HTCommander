/*
Copyright 2026 Ylian Saint-Hilaire

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

   http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace HTCommander.UI.Avalonia.Converters;

/// <summary>
/// Displays a 0-based radio index (channel id, bank/region id) as a 1-based number for
/// humans, while every value sent to the radio stays 0-based on the wire. One-way display
/// only. A negative id (e.g. "no channel" = -1) is shown as an em dash.
/// </summary>
public sealed class PlusOneConverter : IValueConverter
{
    public static readonly PlusOneConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int i) return i < 0 ? "—" : (i + 1).ToString(culture);
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
