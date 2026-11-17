using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core;
using Ohanami.Core.Tags;

namespace Ohanami.App;

/// <summary>
/// 画面が使う手動タグの窓口。製品は Core の <see cref="ManualTags"/>(<see cref="CoreTagService"/>)。
/// テストは偽物に替える(<see cref="ManualTags"/> は Core の内部でしか作れないため、App 側に最小の継ぎ目を置く)。
/// </summary>
internal interface ITagService
{
    /// <summary>選んだ画像に同じタグを付ける(1 トランザクション)。新しく付いた枚数を返す。名前が決まりに合わなければ <see cref="InvalidTagNameException"/>。</summary>
    Task<int> AddToManyAsync(IReadOnlyCollection<string> hashes, string name, CancellationToken ct);

    /// <summary>1 枚からタグを外す。外せたら true。</summary>
    Task<bool> RemoveAsync(string hash, string name, CancellationToken ct);

    /// <summary>画像に付いている手動タグ(名前順)。</summary>
    Task<IReadOnlyList<string>> GetAsync(string hash, CancellationToken ct);

    /// <summary>既存のタグの候補(使われている数の多い順)。<paramref name="exclude"/> の名前は除く。</summary>
    Task<IReadOnlyList<TagCandidate>> GetCandidatesAsync(int limit, IEnumerable<string>? exclude, CancellationToken ct);
}

/// <summary><see cref="ITagService"/> の製品の実装: Core の <see cref="ManualTags"/> にそのまま渡す。</summary>
internal sealed class CoreTagService(ManualTags tags) : ITagService
{
    public Task<int> AddToManyAsync(IReadOnlyCollection<string> hashes, string name, CancellationToken ct) => tags.AddToManyAsync(hashes, name, ct);

    public Task<bool> RemoveAsync(string hash, string name, CancellationToken ct) => tags.RemoveAsync(hash, name, ct);

    public Task<IReadOnlyList<string>> GetAsync(string hash, CancellationToken ct) => tags.GetAsync(hash, ct);

    public Task<IReadOnlyList<TagCandidate>> GetCandidatesAsync(int limit, IEnumerable<string>? exclude, CancellationToken ct) => tags.GetCandidatesAsync(limit, exclude, ct);
}
