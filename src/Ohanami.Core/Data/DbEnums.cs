using System;

namespace Ohanami.Core.Data;

/// <summary>images.format。画像のバイト列の先頭で判定した形式。</summary>
internal enum ImageFormat { Png, Jpeg, Webp }

/// <summary>file_issues.kind。ハッシュまで進めなかったファイルの理由。</summary>
internal enum FileIssueKind { Unreadable, Unsupported }

/// <summary>models.kind。</summary>
internal enum ModelKind { ImageEmbedding, Vlm }

/// <summary>stage_status.status。未処理は行が無い状態で表すので、ここには無い。</summary>
internal enum StageStatus { Done, Failed }

/// <summary>tags.kind。</summary>
internal enum TagKind { Manual, Character }

/// <summary>image_tags.source。</summary>
internal enum TagSource { Manual, Propagated }

/// <summary>
/// 列挙 ↔ DB の文字列の明示的な変換(ADR 0005)。
/// 列挙の列には CHECK 制約を付けないので、「想定外は落とす」はここで守る。
/// すべて <c>switch</c> 式で 1 つずつ書く(Enum.ToString / Enum.Parse や名前の自動対応には頼らない)。
/// 書くときに未定義の値(キャストで作った値)や、読むときに知らない文字列は <see cref="UnknownEnumValueException"/>。
/// 列挙に値を足したら、ここに足さないと網羅のテストが落ちる。
/// </summary>
internal static class DbEnums
{
    // ---- images.format
    public static string ToDb(ImageFormat v) => v switch
    {
        ImageFormat.Png => "png",
        ImageFormat.Jpeg => "jpeg",
        ImageFormat.Webp => "webp",
        _ => throw Undefined(v),
    };

    public static ImageFormat ImageFormatFromDb(string s) => s switch
    {
        "png" => ImageFormat.Png,
        "jpeg" => ImageFormat.Jpeg,
        "webp" => ImageFormat.Webp,
        _ => throw new UnknownEnumValueException("images.format", s),
    };

    // ---- file_issues.kind
    public static string ToDb(FileIssueKind v) => v switch
    {
        FileIssueKind.Unreadable => "unreadable",
        FileIssueKind.Unsupported => "unsupported",
        _ => throw Undefined(v),
    };

    public static FileIssueKind FileIssueKindFromDb(string s) => s switch
    {
        "unreadable" => FileIssueKind.Unreadable,
        "unsupported" => FileIssueKind.Unsupported,
        _ => throw new UnknownEnumValueException("file_issues.kind", s),
    };

    // ---- models.kind
    public static string ToDb(ModelKind v) => v switch
    {
        ModelKind.ImageEmbedding => "image-embedding",
        ModelKind.Vlm => "vlm",
        _ => throw Undefined(v),
    };

    public static ModelKind ModelKindFromDb(string s) => s switch
    {
        "image-embedding" => ModelKind.ImageEmbedding,
        "vlm" => ModelKind.Vlm,
        _ => throw new UnknownEnumValueException("models.kind", s),
    };

    // ---- stage_status.stage(公開の Stage)
    public static string ToDb(Stage v) => v switch
    {
        Stage.Embed => "embed",
        Stage.Ocr => "ocr",
        Stage.Caption => "caption",
        _ => throw Undefined(v),
    };

    public static Stage StageFromDb(string s) => s switch
    {
        "embed" => Stage.Embed,
        "ocr" => Stage.Ocr,
        "caption" => Stage.Caption,
        _ => throw new UnknownEnumValueException("stage_status.stage", s),
    };

    // ---- stage_status.status
    public static string ToDb(StageStatus v) => v switch
    {
        StageStatus.Done => "done",
        StageStatus.Failed => "failed",
        _ => throw Undefined(v),
    };

    public static StageStatus StageStatusFromDb(string s) => s switch
    {
        "done" => StageStatus.Done,
        "failed" => StageStatus.Failed,
        _ => throw new UnknownEnumValueException("stage_status.status", s),
    };

    // ---- runs.job_kind(公開の JobKind)
    public static string ToDb(JobKind v) => v switch
    {
        JobKind.Embed => "embed",
        JobKind.Vlm => "vlm",
        _ => throw Undefined(v),
    };

    public static JobKind JobKindFromDb(string s) => s switch
    {
        "embed" => JobKind.Embed,
        "vlm" => JobKind.Vlm,
        _ => throw new UnknownEnumValueException("runs.job_kind", s),
    };

    // ---- tags.kind
    public static string ToDb(TagKind v) => v switch
    {
        TagKind.Manual => "manual",
        TagKind.Character => "character",
        _ => throw Undefined(v),
    };

    public static TagKind TagKindFromDb(string s) => s switch
    {
        "manual" => TagKind.Manual,
        "character" => TagKind.Character,
        _ => throw new UnknownEnumValueException("tags.kind", s),
    };

    // ---- image_tags.source
    public static string ToDb(TagSource v) => v switch
    {
        TagSource.Manual => "manual",
        TagSource.Propagated => "propagated",
        _ => throw Undefined(v),
    };

    public static TagSource TagSourceFromDb(string s) => s switch
    {
        "manual" => TagSource.Manual,
        "propagated" => TagSource.Propagated,
        _ => throw new UnknownEnumValueException("image_tags.source", s),
    };

    private static UnknownEnumValueException Undefined<T>(T value) where T : struct, Enum =>
        new(typeof(T).Name, $"(未定義の値 {Convert.ToInt64(value)})");
}
