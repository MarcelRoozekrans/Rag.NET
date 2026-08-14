namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// The articles one extraction run will cover, and enough context to say what was left out.
/// <para>
/// <b>The corpus was always fully loaded; only the filter was fixed.</b> The generation tool reads
/// the whole converted dataset — all 609 articles — and then keeps the sixty
/// <see cref="MultiHopRagSliceWalk"/> names. Selecting the full corpus is therefore not a new
/// loader, a second cache or a different chunking: it is this projection declining to filter.
/// That is what makes a full run re-use every extraction the slice already paid for, since chunking
/// is a function of one document's own <see cref="BeirDocument.RetrievalText"/> and the extraction
/// cache is keyed on the rendered prompt.
/// </para>
/// </summary>
/// <param name="Corpus">Which mode produced this selection.</param>
/// <param name="Documents">
/// The articles to extract, <b>in corpus order</b> — the order <c>corpus.jsonl</c> lists them in,
/// for both modes.
/// </param>
/// <param name="AvailableDocumentCount">
/// How many articles the mode names before <c>--max-documents</c> is applied: sixty for the slice,
/// the whole corpus for a full run. Equal to <see cref="Documents"/>' count unless the run is
/// bounded.
/// </param>
/// <param name="CorpusDocumentCount">How many articles the converted dataset holds in total.</param>
/// <param name="JudgedQueryCount">
/// How many judged queries the slice walk consumed to reach its articles, and zero for a full
/// corpus — which is derived from no queries at all.
/// </param>
public sealed record GraphExtractionCorpusSelection(
    GraphExtractionCorpus Corpus,
    IReadOnlyList<BeirDocument> Documents,
    int AvailableDocumentCount,
    int CorpusDocumentCount,
    int JudgedQueryCount)
{
    /// <summary>Chooses the articles one run covers.</summary>
    /// <param name="dataset">The whole converted dataset, as <c>BeirLoader.Load</c> returns it.</param>
    /// <param name="corpus">Which articles to take.</param>
    /// <param name="maxDocuments">
    /// An upper bound on how many are taken, honoured by <b>both</b> modes so that a full-corpus
    /// smoke run is possible without editing anything. <see cref="int.MaxValue"/> for all of them.
    /// </param>
    /// <returns>The selection, with the counts a plan needs to state what it will cost.</returns>
    /// <exception cref="InvalidDataException">
    /// The slice walk did not reach <see cref="MultiHopRagSliceWalk.TargetDocumentCount"/>.
    /// </exception>
    public static GraphExtractionCorpusSelection Select(
        BeirDataset dataset, GraphExtractionCorpus corpus, int maxDocuments)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDocuments);

        return corpus switch
        {
            GraphExtractionCorpus.Slice => SelectSlice(dataset, maxDocuments),
            GraphExtractionCorpus.Full => SelectFull(dataset, maxDocuments),
            _ => throw new ArgumentOutOfRangeException(
                nameof(corpus),
                corpus,
                "There is no third corpus. A mode nothing here recognises would otherwise fall " +
                "through to whichever branch is written first, which is how a run ends up " +
                "extracting articles nobody asked for."),
        };
    }

    /// <summary>Says, in one line, what was selected and what was left out.</summary>
    public string Describe() =>
        Corpus == GraphExtractionCorpus.Slice
            ? FormattableString.Invariant(
                $"Corpus slice: {Documents.Count} of the {AvailableDocumentCount} articles the walk derives from {JudgedQueryCount} judged queries; the corpus holds {CorpusDocumentCount}.")
            : FormattableString.Invariant(
                $"Corpus full: {Documents.Count} of the {CorpusDocumentCount} articles in the corpus.");

    /// <summary>
    /// Derives the pinned slice and takes the documents it names.
    /// </summary>
    /// <remarks>
    /// <b>The count guard stays on this path and only this path.</b> Filling the cache for a
    /// different sixty than the guard reads would cost real money and still miss on every key, so a
    /// walk that lands anywhere but exactly sixty is refused rather than extracted. A full-corpus
    /// run has no such invariant to protect — it takes whatever the corpus holds — which is why the
    /// check is here rather than in <see cref="Select"/>.
    /// </remarks>
    private static GraphExtractionCorpusSelection SelectSlice(BeirDataset dataset, int maxDocuments)
    {
        var (queryIds, documentIds) = MultiHopRagSliceWalk.Derive(dataset);
        if (documentIds.Count != MultiHopRagSliceWalk.TargetDocumentCount)
        {
            throw new InvalidDataException(FormattableString.Invariant(
                $"The slice walk produced {documentIds.Count} articles, not {MultiHopRagSliceWalk.TargetDocumentCount}. Filling the cache for a different slice than the guard reads would cost real money and still miss on every key."));
        }

        return new GraphExtractionCorpusSelection(
            GraphExtractionCorpus.Slice,
            TakeInCorpusOrder(dataset.Documents, documentIds, maxDocuments),
            documentIds.Count,
            dataset.Documents.Count,
            queryIds.Count);
    }

    /// <summary>Takes the corpus itself, in file order, bounded by the limit.</summary>
    private static GraphExtractionCorpusSelection SelectFull(BeirDataset dataset, int maxDocuments)
    {
        var taken = new List<BeirDocument>(Math.Min(dataset.Documents.Count, maxDocuments));
        for (var i = 0; i < dataset.Documents.Count && taken.Count < maxDocuments; i++)
        {
            taken.Add(dataset.Documents[i]);
        }

        return new GraphExtractionCorpusSelection(
            GraphExtractionCorpus.Full,
            taken,
            dataset.Documents.Count,
            dataset.Documents.Count,
            JudgedQueryCount: 0);
    }

    /// <summary>
    /// Projects the walked ids back to documents <b>in corpus order</b>, honouring the limit.
    /// </summary>
    /// <remarks>
    /// Corpus order rather than walk order, and the difference is load bearing for the community
    /// phase. Entities merge in <c>SqliteGraphStore</c> by concatenating descriptions, so the order
    /// documents are ingested in decides what every merged entity description says — which decides
    /// the community report prompts, which are cached. The guard projects the slice the same way
    /// (<c>MultiHopRagSlice.Documents</c> filters the corpus in place), so both build the identical
    /// graph. Walk order would be a second ordering that agrees today and could stop agreeing. It
    /// is also what makes the slice a genuine subsequence of a full-corpus run rather than a
    /// reshuffling of one.
    /// </remarks>
    private static List<BeirDocument> TakeInCorpusOrder(
        IReadOnlyList<BeirDocument> corpus, IReadOnlyList<string> documentIds, int maxDocuments)
    {
        var wanted = new HashSet<string>(documentIds, StringComparer.Ordinal);
        var taken = new List<BeirDocument>(Math.Min(documentIds.Count, maxDocuments));

        for (var i = 0; i < corpus.Count && taken.Count < maxDocuments; i++)
        {
            if (wanted.Contains(corpus[i].Id))
            {
                taken.Add(corpus[i]);
            }
        }

        return taken;
    }
}
