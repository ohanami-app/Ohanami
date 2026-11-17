using System;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Embedding;

namespace Ohanami.Core.Search;

/// <summary>クエリの文字列を、L2 正規化したベクトル(モデルの次元)にする。本番はテキスト側のエンコーダ(CPU。約 0.2 秒)。テストでは偽物に替える。</summary>
internal delegate Task<float[]> QueryEmbedder(string text, CancellationToken ct);

/// <summary>
/// 見た目の経路(SigLIP2): クエリのベクトルと、メモリ上の全ベクトルとの内積(正規化済みなので cosine)を<b>全件</b>について計算し、
/// 全件にスコアを付けて(密のまま)融合に渡す。上位 N 件に切ってから渡さない: 評価の数字(融合 tags あり precision@10 0.477、
/// known-item 29/45。ADR 0033)が全件の順位での融合の結果で、回帰ベンチと比べられるようにするため。Limit は融合の後に適用する(<see cref="SearchService"/>)。
/// </summary>
internal sealed class VisualPath : ISearchPath
{
    public const string Id = "visual";

    private readonly VectorTable _table;
    private readonly QueryEmbedder _embed;

    public VisualPath(VectorTable table, QueryEmbedder embedder)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(embedder);
        _table = table;
        _embed = embedder;
    }

    public string PathId => Id;

    public string DisplayName => "見た目";

    public string? ModelId => _table.ModelId;

    /// <summary>本番の <see cref="QueryEmbedder"/>: トークナイザ(起動時に裏で読む Task)を待ち、テキスト側のエンコーダ(CPU)で埋め込む。</summary>
    public static QueryEmbedder TextEncoderEmbedder(Siglip2TextEncoder encoder, Task<Siglip2Tokenizer> tokenizer)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(tokenizer);
        return async (text, ct) =>
        {
            Siglip2Tokenizer tok = await tokenizer.WaitAsync(ct).ConfigureAwait(false);
            return await Task.Run(() => encoder.EmbedQuery(text, tok), ct).ConfigureAwait(false);
        };
    }

    public async Task<PathSearchResult> SearchAsync(string query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        switch (_table.State)
        {
            case VectorTableState.NotLoaded:
                return PathSearchResult.NotReady("ベクトルの読み込みがまだ始まっていない");
            case VectorTableState.Loading:
                return PathSearchResult.NotReady("ベクトルを読み込み中");
            case VectorTableState.Failed:
                throw new InvalidOperationException("ベクトルの読み込みに失敗している: " + _table.FailureMessage);
        }

        float[] q = await _embed(query, ct).ConfigureAwait(false);
        if (q.Length != _table.Dim) throw new InvalidDataException($"クエリのベクトルの長さが {q.Length}(想定 {_table.Dim})。");
        foreach (float x in q) if (!float.IsFinite(x)) throw new InvalidDataException("クエリのベクトルに有限でない値がある。");

        VectorSnapshot snap = _table.Snapshot();   // この時点までに足されたベクトルだけを、全件
        // 上書きで置き換えられた古い行(snap.Valid が false)は飛ばす。無効な行が無ければ(ふつう)ハッシュの配列をそのまま渡す
        var scores = new double[snap.LiveCount];
        if (snap.AllValid)
        {
            for (int i = 0; i < snap.Count; i++)
            {
                if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
                scores[i] = Dot(q, snap.Vector(i));
            }
            return PathSearchResult.Ok(new ArraySegment<string>(snap.Hashes, 0, snap.Count), scores, i => new VisualDetail(scores[i]));
        }
        var liveHashes = new string[snap.LiveCount];
        int n = 0;
        for (int i = 0; i < snap.Count; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (!snap.Valid[i]) continue;
            liveHashes[n] = snap.Hashes[i];
            scores[n] = Dot(q, snap.Vector(i));
            n++;
        }
        return PathSearchResult.Ok(liveHashes, scores, i => new VisualDetail(scores[i]));
    }

    /// <summary>内積(SIMD)。float で積和して double にする(1152 次元の cosine には十分な精度)。</summary>
    internal static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("長さが違う");
        int i = 0;
        float sum = 0;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            Vector<float> acc = Vector<float>.Zero;
            int last = a.Length - Vector<float>.Count;
            for (; i <= last; i += Vector<float>.Count)
                acc += new Vector<float>(a.Slice(i)) * new Vector<float>(b.Slice(i));
            sum = Vector.Sum(acc);
        }
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}
