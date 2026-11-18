using System.Windows;
using System.Windows.Controls;

namespace Ohanami.App;

/// <summary>
/// 窓の中身の組み立て(ADR 0050)。<see cref="MainWindow"/> が使い、画面を出さないテストも同じ組み立てで寸法を測る(手で写さない)。
/// 上: 左(<see cref="IndexPanel"/> 固定の幅)と右(<see cref="ResultsPanel"/> 残り)。その上に、詳細画面の層(<see cref="ResultsPanel.DetailLayer"/>)を 2 列にまたがって重ねる。
/// 下: 状態の行(<see cref="StatusBarView"/>)。詳細画面の層は状態の行を覆わない(詳細画面の操作の結果は、状態の行に出るため)。
/// </summary>
internal static class MainLayout
{
    public static Grid Build(IndexPanel index, ResultsPanel results, StatusBarView status)
    {
        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(WindowLayout.LeftColumnWidth) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(index, 0);
        Grid.SetColumn(results, 1);
        Grid.SetColumn(results.DetailLayer, 0);
        Grid.SetColumnSpan(results.DetailLayer, 2);   // 左の列・検索ボックス・見出し・一覧のすべてを覆う
        main.Children.Add(index);
        main.Children.Add(results);
        main.Children.Add(results.DetailLayer);       // 後に足したものが前面

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(main, 0);
        Grid.SetRow(status, 1);
        root.Children.Add(main);
        root.Children.Add(status);
        return root;
    }
}
