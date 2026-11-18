using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Ohanami.App;

/// <summary>
/// 一括の領域の下に字下げして並べた区画が、その領域に含まれると示す、細い縦線と区画ごとの短い横線(木の形)。
/// 縦線は、一括の領域の下の縁から、最後の区画の見出しの高さまで。横線は、各区画の見出しの高さから、区画の枠の左の縁まで。
/// 見出しの高さは、描くときに見出しの実際の位置から求める(文字の大きさや拡大率で見出しの高さが変わっても、ずれない)。
/// 描くのは、区画の配置が決まったあとでなければならない(WPF は、部品の配置が済んだ時点で描くので、区画より後ろの子として置く)。
/// 区画の大きさが変わったとき(取り終えて領域が空になる・折り返しが変わる)は、描き直す。
/// </summary>
internal sealed class TreeRail : FrameworkElement
{
    /// <summary>この部品の幅(DIP)。区画は、この分だけ右にずれる。</summary>
    internal const double Indent = 24;

    /// <summary>縦線の左の縁(DIP)。</summary>
    private const double LineX = 11;

    private readonly IReadOnlyList<FrameworkElement> _headers;

    /// <param name="sections">区画(上から順に)の枠と、その中の見出し。</param>
    internal TreeRail(IReadOnlyList<(FrameworkElement Section, FrameworkElement Header)> sections)
    {
        _headers = [.. sections.Select(s => s.Header)];
        foreach ((FrameworkElement section, _) in sections) section.SizeChanged += (_, _) => InvalidateVisual();
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    /// <summary>いまの、各見出しの縦の真ん中の高さ(この部品の上の縁から。整数の DIP に切り下げる)。</summary>
    internal double[] HeaderCentersForTests() => Centers();

    /// <summary>最後に描いたときの、各見出しの縦の真ん中の高さ(まだ描いていなければ空)。いまの高さと同じであることを、テストが確かめる。</summary>
    internal double[] DrawnCentersForTests { get; private set; } = [];

    private double[] Centers() => [.. _headers.Select(h => Math.Floor(h.TranslatePoint(new Point(0, h.ActualHeight / 2), this).Y))];

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_headers.Count == 0) return;
        double[] centers = Centers();
        DrawnCentersForTests = centers;
        Brush line = AppColors.ListBorder;
        drawingContext.DrawRectangle(line, null, new Rect(LineX, 0, 1, centers[^1] + 1));
        foreach (double y in centers)
            drawingContext.DrawRectangle(line, null, new Rect(LineX, y, Indent - LineX, 1));
    }
}
