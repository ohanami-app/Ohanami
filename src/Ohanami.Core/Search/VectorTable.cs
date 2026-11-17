using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;

namespace Ohanami.Core.Search;

/// <summary>メモリ上のベクトルの表の状態。</summary>
internal enum VectorTableState
{
    /// <summary>まだ DB から読み込み始めていない。</summary>
    NotLoaded,

    /// <summary>DB から読み込み中(検索は「準備中」を返す)。</summary>
    Loading,

    /// <summary>読み込み済み。以後、埋め込みが進むたびに <see cref="VectorTable.Add"/> で足す。</summary>
    Ready,

    /// <summary>読み込みに失敗した(理由は <see cref="VectorTable.FailureMessage"/>)。</summary>
    Failed,
}

/// <summary>
/// ある時点のベクトルの表の読み取り専用のビュー。以後 <see cref="VectorTable.Add"/> があっても、この範囲 [0, Count) の中身は変わらない
/// (上書きされた古い行の印も、取った時点のまま)。行 [0, Count) のうち有効なのは <see cref="Valid"/> が true のものだけ
/// (上書きで置き換えられた古い行は false。検索は飛ばす)。
/// </summary>
internal readonly record struct VectorSnapshot(string[] Hashes, float[] Data, bool[] Valid, int Count, int LiveCount, int Dim)
{
    /// <summary>i 番目の行のベクトル(長さ Dim)。</summary>
    public ReadOnlySpan<float> Vector(int i) => Data.AsSpan(i * Dim, Dim);

    /// <summary>無効な行(置き換えられた古い行)が無い。</summary>
    public bool AllValid => LiveCount == Count;
}

/// <summary>
/// 現行の model_id の全ベクトルを、メモリに連続した 1 本の配列で持つ表(1 万件 × 1152 次元 × 4 バイトで約 46MB)。
/// 起動時に DB の <c>image_vectors</c> から (model_id, hash) の索引で読み込み(<see cref="LoadAsync"/>)、埋め込みが進むたびに <see cref="Add"/> で足す。
/// <list type="bullet">
/// <item>ベクトルは書式を検査する: 長さ = 次元 × 4 バイト(モデルの spec_json の dim と照合)、すべて有限、L2 ノルムが 1 に近い。違えば <see cref="InvalidDataException"/>
///   (黙って誤読しない)。例外にするのはこの書式の検査だけ。</item>
/// <item>追加と検索は同時に呼んでよい。検索は <see cref="Snapshot"/> を取り、その範囲だけを読む(追加は範囲の外に書き、配列が足りなければ新しい配列に移す。
///   既存の範囲は書き換えない)。</item>
/// <item>同じハッシュをもう一度足すと<b>上書き</b>(後から来たものが勝つ)。ベクトルが同じなら何もしない。違えば、新しい行を末尾に足し、古い行には無効の印を付ける
///   (検索が読んでいる配列は書き換えない)。モデルを A → B → A と戻して再埋め込みすると、GPU の再計算はビット単位で同じとは限らないので、これは正常な経路。
///   無効の行は、次の <see cref="LoadAsync"/> の始めに詰める。</item>
/// <item><see cref="LoadAsync"/> と <see cref="Add"/> の重なりも同じ規則: 読み込みが始まってから <see cref="Add"/> された画像は、そのあとで読んだ DB の行
///   (読み始めた時点のもの。<see cref="Add"/> より古いかもしれない)で上書きしない。</item>
/// </list>
/// </summary>
internal sealed class VectorTable
{
    private const double NormTolerance = 1e-3;

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private string[] _hashes = [];
    private float[] _data = [];
    private bool[] _valid = [];
    private int _count;                           // 行の数(無効な行を含む)
    private int _live;                            // 有効な行の数 = 画像の数
    private HashSet<string>? _addedDuringLoad;    // 読み込み中に Add された画像(読み込みの行で上書きしない)
    private VectorTableState _state = VectorTableState.NotLoaded;
    private string? _failure;

    public string ModelId { get; }

    /// <summary>ベクトルの次元(models の spec_json の dim)。</summary>
    public int Dim { get; }

    public VectorTable(string modelId, int dim)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (dim <= 0) throw new ArgumentOutOfRangeException(nameof(dim));
        ModelId = modelId;
        Dim = dim;
    }

    public VectorTableState State
    {
        get { lock (_gate) return _state; }
    }

    public string? FailureMessage
    {
        get { lock (_gate) return _failure; }
    }

    /// <summary>画像の数(有効な行の数。上書きでは増えない)。</summary>
    public int Count
    {
        get { lock (_gate) return _live; }
    }

    public bool Contains(string hash)
    {
        lock (_gate) return _index.ContainsKey(hash);
    }

    public VectorSnapshot Snapshot()
    {
        lock (_gate)
        {
            // 有効の印は、あとで上書きされても取った時点のまま見えるよう、コピーを渡す(1 万件で 10KB。ベクトルの 46MB に比べて無視できる)
            bool[] valid = new bool[_count];
            Array.Copy(_valid, valid, _count);
            return new VectorSnapshot(_hashes, _data, valid, _count, _live, Dim);
        }
    }

    /// <summary>
    /// ベクトルを足す(同じハッシュがすでにあれば上書き。後から来たものが勝つ)。新しい行を足した(または置き換えた)ら true、同じハッシュに同じベクトルが
    /// すでにあって何も変えなかったら false。書式が違えば <see cref="InvalidDataException"/>。
    /// </summary>
    public bool Add(string hash, ReadOnlySpan<float> vector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        Validate(hash, vector);
        lock (_gate)
        {
            _addedDuringLoad?.Add(hash);
            return AddLocked(hash, vector);
        }
    }

    /// <summary>
    /// 画像のベクトルを表から外す(見つからない画像の記録を消したあと)。行は書き換えず(Snapshot を読んでいる検索が壊れないように)、
    /// 無効の印を付けて、画像の数(<see cref="Count"/>)から引く。無効の行は、次の <see cref="LoadAsync"/> の始めに詰める。
    /// 読み込み中なら、これから読む DB の行(読み始めた時点のもので、消す前かもしれない)で戻さない。外したら true、表に無ければ false。
    /// </summary>
    public bool Remove(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        lock (_gate)
        {
            _addedDuringLoad?.Add(hash);   // 「読み込みの行で入れない」画像の集合(Add された画像と同じ扱い。外した画像を読み込みが戻さない)
            if (!_index.TryGetValue(hash, out int row)) return false;
            _valid[row] = false;
            _live--;
            _index.Remove(hash);
            return true;
        }
    }

    private bool AddLocked(string hash, ReadOnlySpan<float> vector)
    {
        if (_index.TryGetValue(hash, out int existing))
        {
            if (_data.AsSpan(existing * Dim, Dim).SequenceEqual(vector)) return false;
            // 既存の行は書き換えない(Snapshot を読んでいる検索が壊れたベクトルを読まないように)。新しい行を足して、古い行に無効の印を付ける
            _valid[existing] = false;
            _live--;
        }
        if (_count == _hashes.Length)
        {
            // 足りなければ、新しい配列に移す(古い配列は、それを取った Snapshot がそのまま読める)
            int capacity = Math.Max(1024, _hashes.Length * 2);
            Array.Resize(ref _hashes, capacity);
            Array.Resize(ref _valid, capacity);
            float[] data = new float[checked(capacity * Dim)];
            _data.AsSpan(0, _count * Dim).CopyTo(data);
            _data = data;
        }
        _hashes[_count] = hash;
        vector.CopyTo(_data.AsSpan(_count * Dim, Dim));
        _valid[_count] = true;
        _index[hash] = _count;
        _count++;
        _live++;
        return true;
    }

    /// <summary>
    /// 無効な行を詰める(新しい配列に作り直す。古い配列は、それを取った Snapshot がそのまま読める)。無効な行が無ければ何もしない。
    /// 呼ぶのは <see cref="BeginLoading"/>(= <see cref="LoadAsync"/> の始め)だけ。
    /// </summary>
    private void CompactLocked()
    {
        if (_live == _count) return;
        int capacity = Math.Max(1024, _live);
        var hashes = new string[capacity];
        var valid = new bool[capacity];
        float[] data = new float[checked(capacity * Dim)];
        _index.Clear();
        int n = 0;
        for (int i = 0; i < _count; i++)
        {
            if (!_valid[i]) continue;
            hashes[n] = _hashes[i];
            _data.AsSpan(i * Dim, Dim).CopyTo(data.AsSpan(n * Dim, Dim));
            valid[n] = true;
            _index[hashes[n]] = n;
            n++;
        }
        _hashes = hashes;
        _data = data;
        _valid = valid;
        _count = n;
    }

    private void Validate(string hash, ReadOnlySpan<float> v)
    {
        if (v.Length != Dim) throw new InvalidDataException($"画像 {hash} のベクトルの長さが {v.Length}(想定 {Dim})。");
        double sum = 0;
        foreach (float x in v)
        {
            if (!float.IsFinite(x)) throw new InvalidDataException($"画像 {hash} のベクトルに有限でない値({x.ToString(CultureInfo.InvariantCulture)})がある。");
            sum += (double)x * x;
        }
        double norm = Math.Sqrt(sum);
        if (Math.Abs(norm - 1.0) > NormTolerance)
            throw new InvalidDataException($"画像 {hash} のベクトルの L2 ノルムが {norm.ToString("F6", CultureInfo.InvariantCulture)}(想定 1。正規化されていない値は保存しない)。");
    }

    /// <summary>読み込みの開始を記録する(状態 = Loading。検索は「準備中」を返す)。無効な行を詰める。すでに読み込み中なら例外。<see cref="LoadAsync"/> が呼ぶ。</summary>
    internal void BeginLoading()
    {
        lock (_gate)
        {
            if (_state == VectorTableState.Loading) throw new InvalidOperationException("すでに読み込み中。");
            _state = VectorTableState.Loading;
            _failure = null;
            CompactLocked();
            _addedDuringLoad = new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// 読み込み中に DB から読んだ 1 行を表に入れる(<see cref="LoadAsync"/> が呼ぶ)。読み込みが始まってから <see cref="Add"/> された画像なら、
    /// そちらのほうが新しい(DB の行は読み始めた時点のもので、古いかもしれない)ので捨てる。書式の検査は <see cref="Add"/> と同じ。
    /// </summary>
    internal void ApplyLoadedRow(string hash, ReadOnlySpan<float> vector)
    {
        Validate(hash, vector);
        lock (_gate)
        {
            if (_state != VectorTableState.Loading) throw new InvalidOperationException("読み込み中でないときに、DB の行は入れられない。");
            if (_addedDuringLoad!.Contains(hash)) return;
            AddLocked(hash, vector);
        }
    }

    /// <summary>読み込みの完了を記録する(状態 = Ready)。</summary>
    internal void EndLoading()
    {
        lock (_gate)
        {
            _state = VectorTableState.Ready;
            _addedDuringLoad = null;
        }
    }

    /// <summary>読み込みの中断を記録する(状態 = NotLoaded。失敗ではない。読み込み直せる)。</summary>
    internal void CancelLoading()
    {
        lock (_gate)
        {
            _state = VectorTableState.NotLoaded;
            _addedDuringLoad = null;
        }
    }

    /// <summary>読み込みの失敗を記録する(状態 = Failed)。</summary>
    internal void FailLoading(string message)
    {
        lock (_gate)
        {
            _state = VectorTableState.Failed;
            _failure = message;
            _addedDuringLoad = null;
        }
    }

    /// <summary>
    /// DB の image_vectors から、この表の model_id の全行を読み込む(索引 (model_id, hash) の順 = ハッシュ順)。BLOB の長さが次元 × 4 バイトでなければ例外。
    /// 読み込み中は <see cref="State"/> = Loading(検索は「準備中」)。失敗したら Failed にして例外を投げ直す。中断(キャンセル)は失敗にしない。
    /// 読み込みの始めに、無効な行を詰める。読み込み中に <see cref="Add"/> された画像は、後から来た <see cref="Add"/> が勝つ(読み込みの行で上書きしない)。
    /// </summary>
    public Task LoadAsync(OhanamiDatabase db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return Task.Run(() =>
        {
            BeginLoading();
            try
            {
                if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("ベクトルは float32 のリトルエンディアンで保存する(ビッグエンディアンの環境は未対応)。");
                using SqliteConnection conn = db.CreateReadConnection();
                using SqliteCommand cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT hash, embedding FROM image_vectors WHERE model_id = $model ORDER BY hash";
                cmd.Parameters.AddWithValue("$model", ModelId);
                using SqliteDataReader r = cmd.ExecuteReader();
                int expectedBytes = checked(Dim * sizeof(float));
                var floats = new float[Dim];
                while (r.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    string hash = r.GetString(0);
                    byte[] blob = r.GetFieldValue<byte[]>(1);
                    if (blob.Length != expectedBytes)
                        throw new InvalidDataException($"画像 {hash} のベクトル(model_id = {ModelId})の BLOB が {blob.Length} バイト(想定 {expectedBytes} = 次元 {Dim} × 4)。");
                    MemoryMarshal.Cast<byte, float>(blob).CopyTo(floats);
                    ApplyLoadedRow(hash, floats);
                }
                EndLoading();
            }
            catch (OperationCanceledException)
            {
                CancelLoading();
                throw;
            }
            catch (Exception e)
            {
                FailLoading(e.Message);
                throw;
            }
        }, ct);
    }
}
