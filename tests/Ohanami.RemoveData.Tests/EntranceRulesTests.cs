using System;
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
}
