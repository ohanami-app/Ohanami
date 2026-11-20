using System;
using System.IO;
using Xunit;

namespace Ohanami.RemoveData.Tests;

/// <summary>
/// 入口(<see cref="ScriptEntrance"/>)の安全の決まりが、プロセスを起こす前に効くこと。
/// どれも道具を動かさずに例外で終わる(本物のデータの場所を、テストが消せないことの確認)。
/// </summary>
public sealed class EntranceRulesTests
{
    private static void Refused(params string[] args)
    {
        using TempArea area = TempArea.Create();
        Assert.Throws<InvalidOperationException>(() => ScriptEntrance.RunScript(area.AppFolder, args));
        Assert.Throws<InvalidOperationException>(() => ScriptEntrance.RunCmd(area.AppFolder, args));
    }

    [Fact]
    public void A_delete_mode_without_a_data_root_is_refused() => Refused("-Mode", "All", "-Yes");

    [Fact]
    public void A_delete_mode_whose_data_root_is_outside_the_temp_folder_is_refused() =>
        Refused("-DataRoot", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Ohanami", "-Mode", "All", "-Yes");

    [Fact]
    public void A_delete_mode_whose_data_root_is_relative_is_refused() => Refused("-DataRoot", @"relative\data", "-Mode", "Models", "-Yes");

    [Fact]
    public void A_run_without_a_data_root_must_be_list_only() => Refused("-Yes");

    [Fact]
    public void A_run_whose_data_root_is_outside_the_temp_folder_must_be_list_only() =>
        Refused("-DataRoot", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Ohanami");

    [Fact]
    public void A_list_only_run_without_a_data_root_is_allowed_by_the_entrance_rules()
    {
        // 入口の決まりの確認だけ(動かさない): 通ることは、RemoveDataRunTests の ListOnly の実行で確かめている
        using TempArea area = TempArea.Create();
        Assert.True(ScriptEntrance.IsUnderTempRoot(area.Dir));
        Assert.False(ScriptEntrance.IsUnderTempRoot(ScriptEntrance.TempRoot));
        Assert.False(ScriptEntrance.IsUnderTempRoot(ScriptEntrance.TempRoot + @"-other\x"));
        Assert.False(ScriptEntrance.IsUnderTempRoot(@"relative\data"));
    }

    // PowerShell は、略称(-Mod)・コロンの形(-Mode:All)・位置引数も受ける。入口が「綴りの完全一致」だけを見ていると、これらで守りをすり抜けて、
    // 一時フォルダの外の -DataRoot に対して、消す動きが動く。決めた綴りの外は、動かさずに例外にする
    private static readonly string TempData = Path.Combine(ScriptEntrance.TempRoot, "x", "data");
    private static readonly string OutsideData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Ohanami";

    [Fact]
    public void A_mode_given_in_the_colon_form_is_refused_even_when_the_data_root_is_in_the_temp_folder() => Refused("-Mode:All", "-Yes", "-DataRoot", TempData);

    [Fact]
    public void A_data_root_given_in_the_colon_form_is_refused() => Refused("-DataRoot:" + OutsideData, "-Mode", "All", "-Yes");

    [Fact]
    public void An_abbreviated_parameter_name_is_refused() => Refused("-Mod", "All", "-Yes", "-DataRoot", TempData);

    [Fact]
    public void An_abbreviated_data_root_name_is_refused() => Refused("-Data", OutsideData, "-Mode", "All", "-Yes");

    [Fact]
    public void A_positional_value_is_refused() => Refused("All", "-DataRoot", TempData);

    [Fact]
    public void A_parameter_given_twice_is_refused() => Refused("-DataRoot", TempData, "-DataRoot", OutsideData, "-Mode", "All", "-Yes");

    [Fact]
    public void A_mode_other_than_the_two_the_script_knows_is_still_passed_to_the_script_not_judged_by_the_entrance()
    {
        // 値の正しさは道具の仕事(ValidateSet)。入口は、綴りと根の場所だけを見る
        using TempArea area = TempArea.Create();
        ScriptResult r = ScriptEntrance.RunScript(area.AppFolder, ["-DataRoot", area.Combine("data"), "-Mode", "Nothing", "-Yes"]);
        Assert.NotEqual(0, r.ExitCode);
    }
}
