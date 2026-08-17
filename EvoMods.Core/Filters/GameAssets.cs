using EvoMods.Core.Game;
using EvoMods.Core.Refs;

namespace EvoMods.Core.Filters;

/// <summary>Resolves a game-relative reference against the install, then against its archive.</summary>
/// <remarks>
/// ⚠️ Disk FIRST, and not only for speed. On a packed install the stock <c>natural1/*.curve</c> files
/// every ported filter leans on live inside <c>content.kspkg</c>, so a bare <see cref="File.Exists"/>
/// reports every dependency missing and the install refuses for no reason at all. On an unpacked
/// install the disk answers first and the archive is never opened — the same reasoning as
/// <see cref="ArchiveStockRegistry"/>'s lazy open.
/// </remarks>
public interface IGameAssets
{
    string GameRoot { get; }

    /// <summary>Does this reference resolve at all, on disk or in the archive?</summary>
    bool Has(string reference);

    /// <summary>Its bytes, or null when nothing has them.</summary>
    byte[]? Read(string reference);

    /// <summary>Is it loose on disk, as opposed to only inside the archive?</summary>
    bool OnDisk(string reference);

    /// <summary>The archive half, or the reason there is none — for the report.</summary>
    string StockSource { get; }
}

public sealed class GameAssets(string gameRoot, IStockRegistry? stock = null) : IGameAssets
{
    private readonly IStockRegistry _stock = stock ?? ArchiveStockRegistry.ForGame(gameRoot);

    public string GameRoot => gameRoot;

    public string StockSource => _stock.Describe;

    public bool OnDisk(string reference) => File.Exists(RefPath.RealPath(gameRoot, reference));

    public bool Has(string reference) =>
        OnDisk(reference) || (_stock.Available && _stock.Read(RefPath.Canon(reference)) is not null);

    public byte[]? Read(string reference)
    {
        string real = RefPath.RealPath(gameRoot, reference);
        if (File.Exists(real))
            return File.ReadAllBytes(real);

        return _stock.Available ? _stock.Read(RefPath.Canon(reference)) : null;
    }
}
