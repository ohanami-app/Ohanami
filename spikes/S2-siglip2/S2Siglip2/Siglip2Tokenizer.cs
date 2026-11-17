using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace S2Siglip2;

/// <summary>
/// SigLIP2(Gemma 語彙 256k)の BPE トークナイザ。tokenizer.json(Immich に同梱。参照と同じファイル)の merges だけで動く。
/// Rust の tokenizers(参照)の動作を次の順に再現する:
/// <list type="number">
/// <item>normalized=false の added_tokens を、生のテキスト上で「最も左、同じ位置なら最長」で先に切り出す(Aho-Corasick の LeftmostLongest)</item>
/// <item>それ以外の部分に normalizer(" " → "▁")をかけ、pre_tokenizer(" " で分割。正規化後は空白が無いので全体が1語)を経て BPE にかける</item>
/// <item>BPE: 各文字を語彙の id にする。語彙に無い文字は byte_fallback で UTF-8 バイトごとの &lt;0xNN&gt;、それも無ければ &lt;unk&gt;(fuse_unk で連続を1つに)。
///   その後、rank が最小の merge から順に(同じ rank なら左から)結合する</item>
/// <item>post_processor: 末尾に &lt;eos&gt;。truncation: &lt;eos&gt; の分を引いて 63 token で切る。padding: 64 まで &lt;pad&gt; で埋める</item>
/// </list>
/// 想定外の tokenizer.json(別の normalizer・別の post_processor・normalized=true の added_tokens など)は、黙って誤読せず例外にする。
/// </summary>
public sealed class Siglip2Tokenizer
{
    public const int ContextLength = 64;

    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _vocabSpan;
    private readonly Dictionary<long, (int Rank, int Id)> _merges;
    private readonly int[] _byteTokens;
    private readonly Dictionary<char, (string Content, int Id)[]> _addedByFirstChar;
    private readonly int _unkId;

    public int PadId { get; }
    public int EosId { get; }
    public int VocabSize => _vocab.Count;
    public int MergeCount => _merges.Count;
    public int AddedTokenCount { get; }

    private Siglip2Tokenizer(
        Dictionary<string, int> vocab, Dictionary<long, (int, int)> merges, int[] byteTokens,
        Dictionary<char, (string, int)[]> addedByFirstChar, int addedCount, int unkId, int padId, int eosId)
    {
        _vocab = vocab;
        _vocabSpan = vocab.GetAlternateLookup<ReadOnlySpan<char>>();
        _merges = merges;
        _byteTokens = byteTokens;
        _addedByFirstChar = addedByFirstChar;
        AddedTokenCount = addedCount;
        _unkId = unkId;
        PadId = padId;
        EosId = eosId;
    }

    private static long Key(int a, int b) => ((long)a << 32) | (uint)b;

    // ---------------------------------------------------------------- 読み込み

    /// <summary>直近の Load の内訳(診断用)。</summary>
    public static string LastLoadBreakdown { get; private set; } = "";

    /// <summary>
    /// tokenizer.json を Utf8JsonReader で1回だけ走査して読む(語彙 256k と merges 580k を JsonDocument に載せると遅いため)。
    /// 想定しているキーと値の形だけを受け、それ以外は NotSupportedException にする。
    /// </summary>
    public static Siglip2Tokenizer Load(string tokenizerJsonPath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        byte[] bytes = File.ReadAllBytes(tokenizerJsonPath);
        double tRead = sw.Elapsed.TotalMilliseconds;

        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        ExpectToken(ref reader, JsonTokenType.StartObject, "ルート");

        int eosId = -1;
        bool sawNormalizer = false, sawPre = false, sawPost = false;
        List<(string Content, int Id)>? added = null;
        Dictionary<string, int>? vocab = null;
        Dictionary<long, (int, int)>? merges = null;
        string? unkToken = null;
        double tVocab = 0, tMerges = 0;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string name = reader.GetString()!;
            reader.Read();
            switch (name)
            {
                case "version":
                    Require(reader.TokenType == JsonTokenType.String && reader.GetString() == "1.0", "version は \"1.0\"");
                    break;
                case "truncation":
                case "padding":
                    Require(reader.TokenType == JsonTokenType.Null, $"{name} は null");
                    break;
                case "normalizer":
                    using (JsonDocument d = JsonDocument.ParseValue(ref reader)) ValidateNormalizer(d.RootElement);
                    sawNormalizer = true;
                    break;
                case "pre_tokenizer":
                    using (JsonDocument d = JsonDocument.ParseValue(ref reader)) ValidatePreTokenizer(d.RootElement);
                    sawPre = true;
                    break;
                case "post_processor":
                    using (JsonDocument d = JsonDocument.ParseValue(ref reader)) eosId = ValidatePostProcessor(d.RootElement);
                    sawPost = true;
                    break;
                case "decoder":
                    reader.Skip(); // 復号は使わない
                    break;
                case "added_tokens":
                    using (JsonDocument d = JsonDocument.ParseValue(ref reader)) added = ParseAddedTokens(d.RootElement);
                    break;
                case "model":
                    ParseModel(ref reader, sw, out vocab, out merges, out unkToken, out tVocab, out tMerges);
                    break;
                default:
                    throw new NotSupportedException($"tokenizer.json が想定と違う: 想定外のキー '{name}'");
            }
        }
        Require(sawNormalizer && sawPre && sawPost && added is not null && vocab is not null && merges is not null && unkToken is not null,
            "必要なキー(normalizer, pre_tokenizer, post_processor, added_tokens, model)が揃っていない");

        // byte_fallback 用の <0xNN>。無いものは -1(Rust は「全バイトが揃うとき」だけ byte_fallback を使う)
        var byteTokens = new int[256];
        for (int b = 0; b < 256; b++) byteTokens[b] = vocab!.TryGetValue($"<0x{b:X2}>", out int id) ? id : -1;

        var byFirst = new Dictionary<char, List<(string, int)>>();
        foreach (var (content, id) in added!)
        {
            if (!byFirst.TryGetValue(content[0], out var list)) byFirst[content[0]] = list = new List<(string, int)>();
            list.Add((content, id));
        }
        var addedByFirstChar = new Dictionary<char, (string, int)[]>();
        foreach (var (ch, list) in byFirst)
        {
            list.Sort((x, y) => y.Item1.Length.CompareTo(x.Item1.Length)); // 長い順(同じ位置では最長を採る)
            addedByFirstChar[ch] = list.ToArray();
        }

        double tTotal = sw.Elapsed.TotalMilliseconds;
        LastLoadBreakdown = $"ファイル読み込み {tRead:F0} ms、語彙 {tVocab:F0} ms、merges {tMerges:F0} ms、そのほか {tTotal - tRead - tVocab - tMerges:F0} ms(合計 {tTotal:F0} ms)";
        if (!vocab!.TryGetValue(unkToken!, out int unkId)) throw new InvalidDataException("<unk> が語彙に無い");
        if (!vocab.TryGetValue("<pad>", out int padId)) throw new InvalidDataException("<pad> が語彙に無い");
        return new Siglip2Tokenizer(vocab, merges!, byteTokens, addedByFirstChar, added.Count, unkId, padId, eosId);
    }

    private static void ExpectToken(ref Utf8JsonReader reader, JsonTokenType type, string what)
    {
        if (!reader.Read() || reader.TokenType != type) throw new NotSupportedException($"tokenizer.json が想定と違う: {what} が {type} でない");
    }

    private static List<(string Content, int Id)> ParseAddedTokens(JsonElement el)
    {
        // 想定している形(normalized=false、lstrip/rstrip/single_word=false)だけを受ける
        var added = new List<(string Content, int Id)>();
        foreach (JsonElement t in el.EnumerateArray())
        {
            string content = t.GetProperty("content").GetString()!;
            int id = t.GetProperty("id").GetInt32();
            Require(!t.GetProperty("normalized").GetBoolean(), $"added_token '{content}' は normalized=false");
            Require(!t.GetProperty("lstrip").GetBoolean() && !t.GetProperty("rstrip").GetBoolean(), $"added_token '{content}' は lstrip/rstrip=false");
            Require(!t.GetProperty("single_word").GetBoolean(), $"added_token '{content}' は single_word=false");
            Require(content.Length > 0, "空の added_token");
            added.Add((content, id));
        }
        return added;
    }

    private static void ParseModel(
        ref Utf8JsonReader reader, System.Diagnostics.Stopwatch sw,
        out Dictionary<string, int>? vocab, out Dictionary<long, (int, int)>? merges, out string? unkToken,
        out double tVocab, out double tMerges)
    {
        Require(reader.TokenType == JsonTokenType.StartObject, "model は object");
        vocab = null;
        merges = null;
        unkToken = null;
        tVocab = tMerges = 0;
        bool type = false, fuse = false, byteFallback = false, ignoreMerges = true;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string name = reader.GetString()!;
            reader.Read();
            switch (name)
            {
                case "type":
                    Require(reader.TokenType == JsonTokenType.String && reader.GetString() == "BPE", "model.type は BPE");
                    type = true;
                    break;
                case "dropout":
                case "continuing_subword_prefix":
                case "end_of_word_suffix":
                    Require(reader.TokenType == JsonTokenType.Null, $"model.{name} は null");
                    break;
                case "unk_token":
                    unkToken = reader.GetString();
                    break;
                case "fuse_unk":
                    fuse = reader.GetBoolean();
                    break;
                case "byte_fallback":
                    byteFallback = reader.GetBoolean();
                    break;
                case "ignore_merges":
                    ignoreMerges = reader.GetBoolean();
                    break;
                case "vocab":
                {
                    double t0 = sw.Elapsed.TotalMilliseconds;
                    Require(reader.TokenType == JsonTokenType.StartObject, "model.vocab は object");
                    vocab = new Dictionary<string, int>(256_000 + 1024, StringComparer.Ordinal);
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        string piece = reader.GetString()!;
                        reader.Read();
                        vocab.Add(piece, reader.GetInt32());
                    }
                    tVocab = sw.Elapsed.TotalMilliseconds - t0;
                    break;
                }
                case "merges":
                {
                    Require(vocab is not null, "merges より先に vocab がある");
                    double t0 = sw.Elapsed.TotalMilliseconds;
                    merges = ParseMerges(ref reader, vocab!);
                    tMerges = sw.Elapsed.TotalMilliseconds - t0;
                    break;
                }
                default:
                    throw new NotSupportedException($"tokenizer.json が想定と違う: model の想定外のキー '{name}'");
            }
        }
        Require(type && fuse && byteFallback && !ignoreMerges, "model は BPE で fuse_unk=true、byte_fallback=true、ignore_merges=false");
        Require(vocab is not null && merges is not null && unkToken is not null, "model に vocab / merges / unk_token がある");
    }

    /// <summary>
    /// merges の配列(要素は [a, b] の2要素)を読む。HF の BpeBuilder と同じく、語彙に無いトークンがあれば例外。
    /// 同じペアが複数あれば後のものが勝つ。文字列は確保せず、Span で語彙を引く。
    /// </summary>
    private static Dictionary<long, (int, int)> ParseMerges(ref Utf8JsonReader reader, Dictionary<string, int> vocab)
    {
        Require(reader.TokenType == JsonTokenType.StartArray, "model.merges は配列");
        var span = vocab.GetAlternateLookup<ReadOnlySpan<char>>();
        var merges = new Dictionary<long, (int, int)>(700_000);
        Span<char> bufA = stackalloc char[512];
        Span<char> bufB = stackalloc char[512];
        Span<char> bufC = stackalloc char[1024];
        int rank = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            Require(reader.TokenType == JsonTokenType.StartArray, $"merges[{rank}] は配列");
            reader.Read();
            Require(reader.TokenType == JsonTokenType.String, $"merges[{rank}][0] は文字列");
            int la = reader.CopyString(bufA);
            reader.Read();
            Require(reader.TokenType == JsonTokenType.String, $"merges[{rank}][1] は文字列");
            int lb = reader.CopyString(bufB);
            reader.Read();
            Require(reader.TokenType == JsonTokenType.EndArray, $"merges[{rank}] は2要素");

            if (!span.TryGetValue(bufA[..la], out int a)) throw new InvalidDataException($"merges[{rank}] の '{bufA[..la]}' が語彙に無い");
            if (!span.TryGetValue(bufB[..lb], out int b)) throw new InvalidDataException($"merges[{rank}] の '{bufB[..lb]}' が語彙に無い");
            bufA[..la].CopyTo(bufC);
            bufB[..lb].CopyTo(bufC[la..]);
            if (!span.TryGetValue(bufC[..(la + lb)], out int id)) throw new InvalidDataException($"merges[{rank}] の結合結果が語彙に無い");
            merges[Key(a, b)] = (rank, id);
            rank++;
        }
        return merges;
    }

    private static void Require(bool ok, string what)
    {
        if (!ok) throw new NotSupportedException($"tokenizer.json が想定と違う: {what}");
    }

    private static void ValidateNormalizer(JsonElement n)
    {
        Require(n.GetProperty("type").GetString() == "Replace"
            && n.GetProperty("pattern").GetProperty("String").GetString() == " "
            && n.GetProperty("content").GetString() == "▁", "normalizer は Replace(\" \" → \"▁\")だけ");
    }

    private static void ValidatePreTokenizer(JsonElement p)
    {
        Require(p.GetProperty("type").GetString() == "Split"
            && p.GetProperty("pattern").GetProperty("String").GetString() == " "
            && p.GetProperty("behavior").GetString() == "MergedWithPrevious"
            && !p.GetProperty("invert").GetBoolean(), "pre_tokenizer は Split(\" \", MergedWithPrevious)");
    }

    /// <summary>TemplateProcessing の single が「A + &lt;eos&gt;」であることを確かめ、&lt;eos&gt; の id を返す。</summary>
    private static int ValidatePostProcessor(JsonElement pp)
    {
        Require(pp.GetProperty("type").GetString() == "TemplateProcessing", "post_processor は TemplateProcessing");
        JsonElement single = pp.GetProperty("single");
        Require(single.GetArrayLength() == 2
            && single[0].TryGetProperty("Sequence", out JsonElement seq) && seq.GetProperty("id").GetString() == "A"
            && single[1].TryGetProperty("SpecialToken", out JsonElement st) && st.GetProperty("id").GetString() == "<eos>",
            "post_processor.single は [A, <eos>]");
        JsonElement eos = pp.GetProperty("special_tokens").GetProperty("<eos>");
        Require(eos.GetProperty("ids").GetArrayLength() == 1, "<eos> の ids は1つ");
        return eos.GetProperty("ids")[0].GetInt32();
    }

    // ---------------------------------------------------------------- 符号化

    /// <summary>
    /// clean_text 済みの文字列を、参照(Python の tokenizers)と同じ 64 個の id にする(&lt;eos&gt; 付き、63 token で切り詰め、&lt;pad&gt; で埋める)。
    /// 不正なサロゲートは ArgumentException。
    /// </summary>
    public int[] EncodeCleaned(string cleaned)
    {
        ArgumentNullException.ThrowIfNull(cleaned);
        var content = new List<int>(cleaned.Length + 1);
        int pos = 0;
        int plainStart = 0;
        while (pos < cleaned.Length)
        {
            int matchLen = 0, matchId = 0;
            if (_addedByFirstChar.TryGetValue(cleaned[pos], out var candidates))
            {
                foreach (var (tokenText, id) in candidates)
                {
                    if (cleaned.AsSpan(pos).StartsWith(tokenText, StringComparison.Ordinal))
                    {
                        matchLen = tokenText.Length;
                        matchId = id;
                        break; // 長い順に並べてあるので、最初の一致が最長
                    }
                }
            }
            if (matchLen > 0)
            {
                if (pos > plainStart) EncodePlain(cleaned.AsSpan(plainStart, pos - plainStart), content);
                content.Add(matchId);
                pos += matchLen;
                plainStart = pos;
            }
            else pos++;
        }
        if (cleaned.Length > plainStart) EncodePlain(cleaned.AsSpan(plainStart), content);

        var ids = new int[ContextLength];
        int keep = Math.Min(content.Count, ContextLength - 1); // <eos> の分を引く
        for (int i = 0; i < keep; i++) ids[i] = content[i];
        ids[keep] = EosId;
        for (int i = keep + 1; i < ContextLength; i++) ids[i] = PadId;
        return ids;
    }

    /// <summary>生のクエリから 64 個の id へ(TextCleaner.Clean + EncodeCleaned)。</summary>
    public int[] Encode(string text) => EncodeCleaned(TextCleaner.Clean(text));

    private void EncodePlain(ReadOnlySpan<char> plain, List<int> output)
    {
        // normalizer: " " → "▁"。その後は空白が無いので、pre_tokenizer の結果は全体で1語。
        var normalized = new StringBuilder(plain.Length);
        foreach (char c in plain) normalized.Append(c == ' ' ? '▁' : c);
        BpeWord(normalized.ToString(), output);
    }

    /// <summary>1語の BPE(Rust tokenizers の Model::merge_word と Word::merge_all を写したもの)。</summary>
    private void BpeWord(string word, List<int> output)
    {
        var sid = new List<int>(word.Length);
        int pendingUnk = -1; // fuse_unk: 連続する未知文字を1つにまとめるための保留
        Span<byte> utf8 = stackalloc byte[4];
        int i = 0;
        while (i < word.Length)
        {
            if (!Rune.TryGetRuneAt(word, i, out Rune r))
            {
                throw new ArgumentException($"位置 {i} に不正なサロゲート(U+{(int)word[i]:X4})がある。", nameof(word));
            }
            int len = r.Utf16SequenceLength;
            if (_vocabSpan.TryGetValue(word.AsSpan(i, len), out int id))
            {
                if (pendingUnk >= 0) { sid.Add(pendingUnk); pendingUnk = -1; }
                sid.Add(id);
            }
            else
            {
                int nb = r.EncodeToUtf8(utf8);
                bool all = true;
                for (int k = 0; k < nb; k++) if (_byteTokens[utf8[k]] < 0) { all = false; break; }
                if (all)
                {
                    // 参照(Rust)は、byte_fallback のときに保留中の unk を先に出さない(ここでも同じ順序にする)
                    for (int k = 0; k < nb; k++) sid.Add(_byteTokens[utf8[k]]);
                }
                else
                {
                    // fuse_unk = true: 連続する未知文字は1つの <unk> にまとめる
                    pendingUnk = _unkId;
                }
            }
            i += len;
        }
        if (pendingUnk >= 0) sid.Add(pendingUnk);

        MergeAll(sid, output);
    }

    private void MergeAll(List<int> symbolIds, List<int> output)
    {
        int n = symbolIds.Count;
        if (n == 0) return;
        var sid = symbolIds.ToArray();
        var prev = new int[n];
        var next = new int[n];
        var alive = new bool[n];
        for (int k = 0; k < n; k++)
        {
            prev[k] = k - 1;
            next[k] = k + 1 < n ? k + 1 : -1;
            alive[k] = true;
        }

        // 優先度は (rank, 位置) の小さい順(= 最小 rank、同じ rank なら左から)
        var queue = new PriorityQueue<(int Pos, int NewId), (int Rank, int Pos)>();
        for (int k = 0; k + 1 < n; k++)
        {
            if (_merges.TryGetValue(Key(sid[k], sid[k + 1]), out var m)) queue.Enqueue((k, m.Id), (m.Rank, k));
        }

        while (queue.TryDequeue(out var top, out _))
        {
            int pos = top.Pos;
            if (!alive[pos]) continue;
            int nx = next[pos];
            if (nx < 0) continue;
            // 期限切れのエントリを捨てる
            if (!_merges.TryGetValue(Key(sid[pos], sid[nx]), out var cur) || cur.Id != top.NewId) continue;

            sid[pos] = top.NewId;
            alive[nx] = false;
            int nn = next[nx];
            next[pos] = nn;
            if (nn >= 0) prev[nn] = pos;

            int pv = prev[pos];
            if (pv >= 0 && _merges.TryGetValue(Key(sid[pv], sid[pos]), out var left)) queue.Enqueue((pv, left.Id), (left.Rank, pv));
            if (nn >= 0 && _merges.TryGetValue(Key(sid[pos], sid[nn]), out var right)) queue.Enqueue((pos, right.Id), (right.Rank, pos));
        }

        for (int k = 0; k < n; k++) if (alive[k]) output.Add(sid[k]);
    }
}
