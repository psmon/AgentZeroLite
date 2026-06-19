using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using AgentZeroAvalonia.Models;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>채팅 버블 정렬/색상용 정적 IValueConverter 모음.</summary>
public static class Converters
{
    /// <summary>IsUser(bool) → HorizontalAlignment(Right/Left).</summary>
    public static readonly IValueConverter BoolToRightLeft =
        new FuncValueConverter<bool, HorizontalAlignment>(
            isUser => isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left);

    /// <summary>ChatRole → 버블 배경 브러시.</summary>
    public static readonly IValueConverter RoleToBrush =
        new FuncValueConverter<ChatRole, IBrush>(role => role switch
        {
            ChatRole.User      => new SolidColorBrush(Color.FromRgb(0x2D, 0x4A, 0x7A)),
            ChatRole.Assistant => new SolidColorBrush(Color.FromRgb(0x33, 0x38, 0x40)),
            _                  => new SolidColorBrush(Color.FromRgb(0x4A, 0x3A, 0x20)),
        });
}
