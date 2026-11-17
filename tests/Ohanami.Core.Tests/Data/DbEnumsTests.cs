using System;
using System.Collections.Generic;
using System.Linq;
using Ohanami.Core.Data;
using Xunit;

namespace Ohanami.Core.Tests.Data;

/// <summary>
/// 列挙 ↔ DB の文字列の明示的な変換(決定 A)。全メンバーが往復すること、網羅していること、未定義・未知の値で例外になること。
/// 列挙にメンバーを足して変換を書き忘れると、ここが落ちる。
/// </summary>
public sealed class DbEnumsTests
{
    private static void CheckRoundTrip<T>(Func<T, string> toDb, Func<string, T> fromDb, string columnName) where T : struct, Enum
    {
        T[] all = Enum.GetValues<T>();
        Assert.NotEmpty(all);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (T value in all)
        {
            string db = toDb(value); // 変換を書き忘れたメンバーはここで例外
            Assert.False(string.IsNullOrEmpty(db));
            Assert.Equal(db, db.ToLowerInvariant()); // DB の文字列は小文字
            Assert.True(seen.Add(db), $"{typeof(T).Name}: DB の文字列「{db}」が重複している");
            Assert.Equal(value, fromDb(db));
        }

        // 未定義の値(キャストで作った値)は書かない
        T undefined = (T)Enum.ToObject(typeof(T), 12345);
        Assert.False(Enum.IsDefined(undefined));
        var w = Assert.Throws<UnknownEnumValueException>(() => toDb(undefined));
        Assert.Equal(typeof(T).Name, w.Where);

        // 知らない文字列・空・大文字小文字違いは読まない
        foreach (string bad in new[] { "", "unknown", "avif", all.Length > 0 ? toDb(all[0]).ToUpperInvariant() : "X", " " + toDb(all[0]) })
        {
            var e = Assert.Throws<UnknownEnumValueException>(() => fromDb(bad));
            Assert.Equal(columnName, e.Where);
            Assert.Equal(bad, e.Value);
        }
    }

    [Fact] public void ImageFormat_round_trips_and_rejects_unknown() => CheckRoundTrip<ImageFormat>(DbEnums.ToDb, DbEnums.ImageFormatFromDb, "images.format");
    [Fact] public void FileIssueKind_round_trips_and_rejects_unknown() => CheckRoundTrip<FileIssueKind>(DbEnums.ToDb, DbEnums.FileIssueKindFromDb, "file_issues.kind");
    [Fact] public void ModelKind_round_trips_and_rejects_unknown() => CheckRoundTrip<ModelKind>(DbEnums.ToDb, DbEnums.ModelKindFromDb, "models.kind");
    [Fact] public void Stage_round_trips_and_rejects_unknown() => CheckRoundTrip<Stage>(DbEnums.ToDb, DbEnums.StageFromDb, "stage_status.stage");
    [Fact] public void StageStatus_round_trips_and_rejects_unknown() => CheckRoundTrip<StageStatus>(DbEnums.ToDb, DbEnums.StageStatusFromDb, "stage_status.status");
    [Fact] public void JobKind_round_trips_and_rejects_unknown() => CheckRoundTrip<JobKind>(DbEnums.ToDb, DbEnums.JobKindFromDb, "runs.job_kind");
    [Fact] public void TagKind_round_trips_and_rejects_unknown() => CheckRoundTrip<TagKind>(DbEnums.ToDb, DbEnums.TagKindFromDb, "tags.kind");
    [Fact] public void TagSource_round_trips_and_rejects_unknown() => CheckRoundTrip<TagSource>(DbEnums.ToDb, DbEnums.TagSourceFromDb, "image_tags.source");

    [Fact]
    public void Exception_message_names_the_column_and_value()
    {
        var e = Assert.Throws<UnknownEnumValueException>(() => DbEnums.StageFromDb("unknown-stage"));
        Assert.Contains("stage_status.stage", e.Message);
        Assert.Contains("unknown-stage", e.Message);
    }
}
