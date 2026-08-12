namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The fixed sixty-article corner of MultiHop-RAG the GraphRAG guard runs on, and the twenty-seven
/// queries it was derived from — both pinned as literals rather than recomputed.
/// <para>
/// <b>The slice is chosen by queries, and that is the whole of its design.</b> A MultiHop-RAG
/// question cites two to four articles <i>because those articles share entities</i> — the same
/// person, company or event reported by two outlets — which is exactly the cross-article recurrence
/// community detection has to find something in. Sixty articles taken off the top of
/// <c>corpus.jsonl</c> would share nothing in particular, community detection would return sixty
/// disconnected islands, and global search would pass while finding nothing. So the walk is over
/// <c>qrels/test.tsv</c> in query-id order, accumulating each query's evidence documents until
/// sixty distinct articles have been seen; the query that crosses sixty is taken whole.
/// </para>
/// <para>
/// <b>And it supplies ground truth, which is what makes the guard a guard.</b> Every query below is
/// judged, so <see cref="GraphRagFunctionsTests"/> can assert that local search over the graph
/// retrieves one of a query's <i>known-relevant</i> documents rather than merely returning
/// something. A slice picked any other way would have had to assert that results exist, which a
/// pipeline returning the wrong sixty documents also satisfies.
/// </para>
/// <para>
/// <b>Pinned, not derived at run time.</b> The walk above is reproducible and it is written down in
/// this remark, but running it inside the guard would let the slice move whenever upstream moved —
/// and a graph built from a different sixty articles is a different experiment reported under the
/// same name. <see cref="MultiHopRagSliceTests"/> re-derives the walk against the converted dataset
/// and asserts it lands here, so the two cannot drift apart silently: the pin is checked rather
/// than merely asserted to be right once.
/// </para>
/// <para>
/// Derived on 2026-08-12 from <c>multihop-rag</c> at
/// <see cref="MultiHopRagSource.Revision"/>, whose <c>qrels/test.tsv</c> holds 5,908 judgements
/// over 2,255 queries.
/// </para>
/// </summary>
public static class MultiHopRagSlice
{
    /// <summary>The dataset the slice is cut from, as <c>BeirDatasetDescriptor.All</c> names it.</summary>
    public const string DatasetName = MultiHopRagSource.DatasetName;

    /// <summary>
    /// How many distinct articles the walk stops at — the "roughly sixty" the design asks for, and
    /// exactly sixty as it happens, because query <c>mhr-0031</c>'s evidence completes the set
    /// without overshooting it.
    /// </summary>
    /// <remarks>
    /// A constant so the walk in <see cref="MultiHopRagSliceTests"/> and the pinned list below are
    /// bounded by one number rather than by two that could disagree.
    /// </remarks>
    public const int TargetDocumentCount = 60;

    /// <summary>
    /// The twenty-seven judged queries the walk consumed, in id order.
    /// </summary>
    /// <remarks>
    /// Not the first twenty-seven ids: <c>mhr-0010</c>, <c>mhr-0013</c>, <c>mhr-0018</c>,
    /// <c>mhr-0020</c> and <c>mhr-0024</c> are <c>null_query</c> records — answered "Insufficient
    /// information." with an empty evidence list — so <c>qrels/test.tsv</c> judges none of them and
    /// the walk never sees them. That gap is the dataset's shape showing through, not a dropped
    /// row.
    /// </remarks>
    public static IReadOnlyList<string> QueryIds { get; } =
    [
        "mhr-0000", "mhr-0001", "mhr-0002", "mhr-0003", "mhr-0004", "mhr-0005", "mhr-0006",
        "mhr-0007", "mhr-0008", "mhr-0009", "mhr-0011", "mhr-0012", "mhr-0014", "mhr-0015",
        "mhr-0016", "mhr-0017", "mhr-0019", "mhr-0021", "mhr-0022", "mhr-0023", "mhr-0025",
        "mhr-0026", "mhr-0027", "mhr-0028", "mhr-0029", "mhr-0030", "mhr-0031",
    ];

    /// <summary>
    /// The sixty articles those queries cite, in the order the walk first reached them.
    /// </summary>
    /// <remarks>
    /// The ids are article URLs, which is what <c>MultiHopRagConversion</c> assigns: bijective over
    /// all 609 documents, self-describing, and independent of file ordering, so a reordered
    /// upstream cannot silently remap them. Listed in walk order rather than sorted, because that
    /// is the order the derivation produced and it keeps articles cited by the same query adjacent
    /// — readable evidence that the slice really is query-derived. The order is not itself pinned:
    /// <see cref="MultiHopRagSliceTests"/> compares the re-derived articles as a set, since within
    /// one query the qrels order reaches it through a dictionary's enumeration.
    /// </remarks>
    public static IReadOnlyList<string> DocumentIds { get; } =
    [
        "https://www.theverge.com/2023/9/28/23893269/ftx-sam-bankman-fried-trial-evidence-crypto",
        "https://techcrunch.com/2023/10/01/ftx-lawsuit-timeline/",
        "https://techcrunch.com/2023/10/07/sam-altman-backs-a-teens-startup-google-unveils-the-pixel-8-and-tiktok-tests-an-ad-free-tier/",
        "https://fortune.com/2023/09/26/donald-trump-fraud-banks-insurers-real-estate-judge-new-york/",
        "https://www.theage.com.au/business/companies/the-777-million-surprise-donald-trump-is-getting-richer-20231108-p5eicf.html?ref=rss&utm_medium=rss&utm_source=rss_business",
        "https://fortune.com/2023/11/18/how-did-openai-fire-sam-altman-greg-brockman-rogue-board/",
        "https://techcrunch.com/2023/11/17/wtf-is-going-on-at-openai-sam-altman-fired/",
        "https://techcrunch.com/2023/10/08/heres-how-rainforest-an-atlanta-fintech-startup-and-stripe-rival-aims-to-win-over-software-companies/",
        "https://www.nbcnews.com/news/us-news/epoch-times-falun-gong-growth-rcna111373",
        "https://www.cbssports.com/general/news/2023-kentucky-online-sports-betting-sites-best-legal-sportsbooks-promos-bonuses-mobile-apps-how-to-bet/",
        "https://www.sportingnews.com/us/betting/news/nba-roty-odds/7af3a635d324b28ff4c554fd",
        "https://www.sportingnews.com/us/betting/news/vermont-sportsbook-promos/b4779ea9b7172cc57a201b0f",
        "https://fortune.com/crypto/2023/10/04/sam-bankman-fried-lawyers-opening-statements-witnesses-marc-antoine-julliard-adam-yedidia-ftx-commodities-trader-developer/",
        "https://techcrunch.com/2023/10/26/twitch-mike-minton-chief-monetization-officer-twitchcon-2023-partner-plus-revenue-split/",
        "https://techcrunch.com/2023/12/11/beeper-mini-is-back-in-operation-after-apples-attempt-to-shut-it-down/",
        "https://theathletic.com/5133811/2023/12/14/buffalo-bills-jordan-poyer-ayahuasca/",
        "https://www.sportingnews.com/us/nfl/news/nfl-power-rankings-lions-49ers-texans-jets-patriots/7b7ee6be935efc3c735d72ac",
        "https://techcrunch.com/2023/11/21/how-the-openai-fiasco-could-bolster-meta-and-the-open-ai-movement/",
        "https://techcrunch.com/2023/11/30/one-year-later-chatgpt-is-still-alive-and-kicking/",
        "https://techcrunch.com/2023/09/28/chatgpt-everything-to-know-about-the-ai-chatbot/",
        "https://techcrunch.com/2023/10/30/5-things-we-learned-so-far-about-the-google-antitrust-case/",
        "https://www.theverge.com/2023/9/26/23891037/apple-eddy-cue-testimony-us-google",
        "https://techcrunch.com/2023/12/15/news-publisher-files-class-action-antitrust-suit-against-google-citing-ais-harms-to-their-bottom-line/",
        "https://www.theage.com.au/sport/tennis/biggest-win-of-my-career-de-minaur-popyrin-power-australia-into-davis-cup-final-20231125-p5emr5.html?ref=rss&utm_medium=rss&utm_source=rss_sport",
        "https://www.sportingnews.com/us/rugby-union/news/watch-england-vs-south-africa-stream-channel-rugby-world-cup/20ce7b361138e7c1a1574572",
        "https://techcrunch.com/2023/10/30/cruise-hits-the-brakes-on-driverless-uaw-makes-progress-and-more-ev-backpedaling/",
        "https://www.independent.co.uk/arts-entertainment/tv/news/crown-season-6-fact-fiction-diana-b2449430.html",
        "https://www.independent.co.uk/life-style/royal-family/news/princess-diana-st-tropez-b2449049.html",
        "https://techcrunch.com/2023/11/27/eu-amazon-irobot-statement-of-objections/",
        "https://techcrunch.com/2023/11/29/beuc-cpc-meta-complaint/",
        "https://techcrunch.com/2023/12/11/eu-ai-act-gpai-rules-evolve/",
        "https://techcrunch.com/2023/12/18/x-dsa-probe/",
        "https://www.smh.com.au/business/markets/asx-set-to-drop-as-wall-street-s-september-slump-deepens-20230927-p5e7v7.html?ref=rss&utm_medium=rss&utm_source=rss_business",
        "https://www.wired.com/story/best-october-prime-day-deals-2023-5/",
        "https://www.theverge.com/21539047/best-amazon-kindle-deals",
        "https://www.cnbc.com/2023/10/06/amazon-sellers-sound-off-on-the-ftcs-long-overdue-antitrust-case.html",
        "https://www.theverge.com/features/23931789/seo-search-engine-optimization-experts-google-results",
        "https://www.theverge.com/23944251/epic-google-antitrust-trial-explainer-monopoly",
        "https://www.sportingnews.com/us/nfl/news/cowboys-49ers-live-score-highlights-sunday-night-football/e46d8e03c6a948aa82f859c3",
        "https://www.polygon.com/what-to-watch/23959772/best-noir-movies-watch-streaming-neo-noir",
        "https://www.sportingnews.com/us/betting/news/best-sportsbook-bonus-offers-eagles-seahawks-mnf-bet365-betmgm-betrivers-caesars-draftkings-fanduel%C2%A0/0cac736f90d80f21daafd8a2",
        "https://www.sportingnews.com/us/nfl/news/best-chiefs-jets-prop-stars-bets-fanduel-kelce-swift-snf/581275901fe0e3011cdcb562",
        "https://www.engadget.com/the-steam-deck-oled-arrives-november-16-with-an-improved-screen-and-longer-battery-life-180032945.html?src=rss",
        "https://www.polygon.com/reviews/23950861/steam-deck-oled-review-valve-handheld-gaming",
        "https://www.engadget.com/steam-deck-oled-review-its-just-better-180038030.html?src=rss",
        "https://www.independent.co.uk/life-style/jada-pinkett-will-smith-prenup-b2430529.html",
        "https://www.independent.co.uk/life-style/will-smith-jada-separation-divorce-b2429576.html",
        "https://www.independent.co.uk/life-style/taylor-swift-travis-kelce-tweets-b2453410.html",
        "https://www.cbssports.com/nfl/news/taylor-swift-travis-kelce-timeline-everything-to-know-about-rumored-romance-between-pop-star-chiefs-te/",
        "https://www.independent.co.uk/life-style/taylor-swift-time-person-of-the-year-b2459419.html",
        "https://www.theage.com.au/technology/is-google-search-better-than-the-rest-and-is-that-fair-20231012-p5ebsk.html?ref=rss&utm_medium=rss&utm_source=rss_technology",
        "https://techcrunch.com/2023/10/25/amazon-brings-conversational-ai-to-kids-with-launch-of-explore-with-alexa/",
        "https://www.theage.com.au/technology/mass-breach-of-privacy-tiktok-under-fire-for-tracking-users-online-20231224-p5etik.html?ref=rss&utm_medium=rss&utm_source=rss_technology",
        "https://www.cnbc.com/2023/09/28/nike-nke-earnings-q1-2024.html",
        "https://fortune.com/2023/10/06/home-price-outlook-bank-of-america-housing-recession-turbulence-1980s-not-2008/",
        "https://www.independent.co.uk/travel/skiing/best-ski-holidays-canada-b2432416.html",
        "https://www.independent.co.uk/travel/skiing/best-luxury-ski-resorts-b2427066.html",
        "https://www.theverge.com/22307388/wireless-earbuds-deals-airpods-galaxy-buds-powerbeats",
        "https://www.engadget.com/best-october-amazon-prime-day-deals-100020186.html?src=rss",
        "https://techcrunch.com/2023/11/03/why-mozilla-is-betting-on-a-decentralized-social-networking-future/",
    ];

    /// <summary>Gets <see cref="DocumentIds"/> as a set, for membership questions.</summary>
    public static IReadOnlySet<string> DocumentIdSet { get; } =
        new HashSet<string>(DocumentIds, StringComparer.Ordinal);

    /// <summary>Gets <see cref="QueryIds"/> as a set, for membership questions.</summary>
    public static IReadOnlySet<string> QueryIdSet { get; } =
        new HashSet<string>(QueryIds, StringComparer.Ordinal);

    /// <summary>Keeps only the slice's documents, in corpus order.</summary>
    /// <param name="documents">The whole converted corpus.</param>
    /// <returns>The sixty pinned articles.</returns>
    /// <exception cref="InvalidOperationException">
    /// The corpus does not hold all sixty. Silently returning fewer would build the graph from a
    /// smaller corner and report the result under this slice's name.
    /// </exception>
    public static IReadOnlyList<BeirDocument> Documents(IReadOnlyList<BeirDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var wanted = DocumentIdSet;
        var kept = new List<BeirDocument>(DocumentIds.Count);
        for (var i = 0; i < documents.Count; i++)
        {
            if (wanted.Contains(documents[i].Id))
            {
                kept.Add(documents[i]);
            }
        }

        return kept.Count == DocumentIds.Count
            ? kept
            : throw new InvalidOperationException(
                FormattableString.Invariant($"""
                    The corpus holds {kept.Count} of this slice's {DocumentIds.Count} pinned articles.
                    The slice is pinned against {DatasetName} at revision {MultiHopRagSource.Revision};
                    a corpus missing some of it is either a different revision or a short conversion,
                    and building the graph from what is left would report a smaller experiment under
                    this slice's name.
                    """));
    }

    /// <summary>Keeps only the slice's queries, in <c>queries.jsonl</c> order.</summary>
    /// <param name="queries">Every query in the dataset, judged or not.</param>
    /// <returns>The twenty-seven pinned queries.</returns>
    /// <exception cref="InvalidOperationException">The dataset does not hold all twenty-seven.</exception>
    public static IReadOnlyList<BeirQuery> Queries(IReadOnlyList<BeirQuery> queries)
    {
        ArgumentNullException.ThrowIfNull(queries);

        var wanted = QueryIdSet;
        var kept = new List<BeirQuery>(QueryIds.Count);
        for (var i = 0; i < queries.Count; i++)
        {
            if (wanted.Contains(queries[i].Id))
            {
                kept.Add(queries[i]);
            }
        }

        return kept.Count == QueryIds.Count
            ? kept
            : throw new InvalidOperationException(
                FormattableString.Invariant($"""
                    The dataset holds {kept.Count} of this slice's {QueryIds.Count} pinned queries.
                    Without all of them the guard's ground-truth assertion is being made against a
                    query set nobody pinned.
                    """));
    }
}
