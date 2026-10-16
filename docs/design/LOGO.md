# Brand Mark — `Rag.NET`

**Product:** `Rag.NET` · **Mark type:** `geometric` · **Generated:** `2026-10-16` · **Design system:** `absent — no docs/design/MASTER.md in the repository`

> The record for one mark, saved at `docs/design/LOGO.md`. `logo-concept` fills it in; `logo-review` audits against it. Every value the skill's reference files ask to be written down has a slot below, and an empty slot is a finding rather than a silence.
>
> Drawn as a sibling of the Minos.NET mark (`MarcelRoozekrans/Minos.NET`, `docs/design/LOGO.md`): the same 256 grid, the same declared weight 24 for the master and 32 for the favicon, a single colour of the same luminance, the same typeface, tracking, lockup cap heights and stacked artboard. What differs is the idea and the colour.

## Recording conventions

Tokens per the template: a value · `n/a — <why>` · `declined — <why>` · `UNRUN — <what would decide it>`. Citations name a reference file and a section. `grep UNRUN` over this file lists everything this record admits it does not know.

## Concept & rationale

The mark is a ring with its top quarter lifted clear of it: the ring is the corpus, the slots that free the quarter are the chunking, and the quarter standing above its own gap is the chunk that was retrieved. The gap in the ring is exactly as wide as the piece above it, so the eye reads them as one shape pulled apart rather than two shapes placed together. For a library whose job is to find the right piece of a document and hand it to a model, the mark is the retrieval itself, not a picture of a database or a brain.

| Field | Record |
|---|---|
| The brief, in one line | Request: "Can we work on a logo for this project ? Can you look at the Minos.NET package and i want this project to fit in that style." · Q1, the string set in type: "Rag.NET" · Q2, what it does: "A modular RAG (Retrieval-Augmented Generation) pipeline library for .NET: document ingestion, chunking, vector storage, retrieval, and chat with streaming support." — proposed from README.md, confirmed unchanged · Q3, mark type: "Geometric" — the maintainer's explicit choice, matching Minos.NET · Q4, where it must survive: favicon (16–32 px), NuGet / app icon, dark UI; not print, not large format · Q5, must-avoid: "No AI clichés" (no brains, sparkles, neural-net node graphs, chat bubbles), "No database/cylinder icons" (no DB cylinders, no folded-corner documents), "No literal rag/cloth" · Colour: direction "Verdigris sibling" — a patina teal at the luminance of Minos bronze · Typeface: "Bahnschrift 600", to match Minos.NET |
| Why this candidate won | The maintainer chose A ("Retrieved chunk"), no blend. It is the only candidate whose form carries the retrieval rather than a neighbouring idea, it is built from arcs on Minos.NET's radii (r 96 outer, weight 24) so it sits beside the coil as a sibling, and it holds at 16 px on both grounds. |
| Candidates rejected | B — The serpentine: reads as the digit `2` at every size (C8), a glyph that is not in the name. · C — The indexer `[■]`: the crispest reproduction of the three, but `[■]` is a common code / stop glyph, low distinctiveness (C8), and the furthest from Minos.NET's curved family. |
| Keywords present from anti-slop.md pattern 6's list | none — the scan over the whole brief above finds none of the eight |
| Type chosen because | Geometric was the maintainer's explicit Q3 answer, and also what SKILL.md § "You choose" is derived, not chosen reaches: Q1's string is long (6 characters ignoring the stop) and Q4 is size-hostile (favicon, app icon), so a mark-alone variant is mandatory, and the initials `R` / `RN` are not distinctive enough to carry a monogram — mark-types.md § Choosing the type, row 1. |
| Mark–name relationship | the mark does not reference the name — the derivation produced a mandatory mark-alone variant; the maintainer chose geometric explicitly, as for Minos.NET, so no letter of "Rag.NET" is drawn. The name is carried by the wordmark and the two lockups, set in type. |

### Step 0 records

| Field | Record |
|---|---|
| Existing-mark guard | nothing at `assets/brand/`, `public/brand/`, `wwwroot/brand/`, `public/favicon.*`, `static/logo.*` → `logo-concept`, full flow. `docusaurus.config.ts` names `favicon: 'img/favicon.ico'`, but the repository has no `static/` directory, so the file does not exist. `Directory.Build.props` records "No PackageIcon: the repository has no icon asset." |
| Asset directory | `assets/brand/` — a .NET library, not a web app (SKILL.md Step 0 item 5); the same directory Minos.NET uses |
| Typeface (Step 0 item 8) | Bahnschrift 600, the maintainer's choice to match Minos.NET. **Does not resolve on the machine that drew this set** (Linux; Bahnschrift ships with Windows). The cost was stated before the run and accepted: the seven type-dependent slots below are `UNRUN`. |
| Where the design record lives | `docs/design/`, as in Minos.NET. The documentation site publishes every file under `docs/`, so `design/**` was added to the `exclude` list in `docusaurus.config.ts`, beside `plans/**` and `planning/**`: a working record, not product documentation. |

### Question 4's consequences, taken before drawing

| Context | Consequence |
|---|---|
| favicon | reproduction.md § The counter floor, in aggregate, condition 3 unmet → every counter built to the target; `logo-favicon.svg` ships as a redraw |
| dark UI | condition 2 unmet → counters at `max(1.25 w, 32) + r · w`, r = 3%: 32.72 at the master's w 24, 40.96 at the favicon's w 32 |
| app icon | square-tile reading, route **composite at export** — no drawn container; correction 6 `n/a` |
| one-colour print / embroidery, large format | not named → no print rows, correction 4 judged at 64 / 256 only |

### Joint-satisfiability check (before Step 2)

`max(1.25 w, 32) + 0.03 w` = 32.48 / 32.72 / 40.96 at w 16 / 24 / 32. No letterform is drawn, so the two-stem width line does not bind; the space was not collapsed.

### Step 2 — the three directions, as written before drawing

Set aside before drawing, per Q5 and anti-slop.md: a brain, spark or chat bubble (Q5); a database cylinder or folded-corner page (Q5); a rag or cloth (Q5, and pictorial — refusal gate); connected dots for embeddings or a knowledge graph (pattern 5); a magnifying glass (pictorial, and the universal search icon); concentric arcs radiating from a point (reads as the Wi-Fi icon).

| | Idea | Seed | Gives up |
|---|---|---|---|
| **A — The retrieved chunk (chosen)** | the corpus as a ring, cut into chunks; the retrieved one lifted out | ring, outer r 96 / inner r 72 about (128,144), w 24 | an open ring at the top sits near the power-symbol family; a lifted piece has to read as lifted, not as one more segment |
| B — The serpentine | a document read line by line: one stroke over three rows, Minos.NET's alternating semicircles with the radius held | a run 32 thick from x 16, top edge y 32, w 32 | serpentines read as letters or digits |
| C — The indexer | `chunks[k]`: brackets (the context window) holding one chunk | square ring 16 … 240, w 24, opened where the chunk's edges fall | a code glyph, the least distinctive; no curves, so the weakest tie to Minos.NET |

## Construction

### The rule, and the derivation chain

**Seed:** a ring of outer radius 96 and inner radius 72 about (128, 144), declared weight 24 — Minos.NET's outer radius and weight.

**Rule:** two straight slots, 40 wide and centred on the two upper diagonals, free the ring's top quarter; that quarter is lifted 32 (two grid units) straight up.

| Element | Derived from | Arithmetic | Result |
|---|---|---|---|
| Ring, the corpus | seed | edges r 96 / r 72; inner ry 72 + 0.96 = 72.96 (correction 4, outer held) | outer x 32 / 224, bottom y 240; inner bottom y 216.96 |
| Slots, the chunking | the counter target | 32.72 at w 24 + dark → 40 (2.5 grid units); edges are lines 20 either side of the 45° and 135° diagonals through (128,144) | slot edges meet r 96 at x 75.75 / 180.25, 47.47 / 208.53, and r 72 at x 92.7 / 163.3 (chunk side, ry 72.96), 64.79 / 191.21 (ring side) |
| The chunk, retrieved | the ring's top quarter, between the slots | lifted 32 | outer top y 144 − 96 − 32 = 16; inner top y 144 − 72.96 − 32 = 39.04 |
| The gap it leaves | the chunk's own outline | — | the ring's opening is the chunk's footprint; centred on 90°, never placed |
| Slot after the lift | slot 40 widened by the lift | 40 + 32 · sin 45° = 40 + 22.63 | 62.63 between chunk and ring, the narrowest negative space |
| Vertical placement | ink extents | cy = 128 + 32 / 2 = 144 | ink y 16 … 240, centred on 128 |

### Optical exceptions

| Variant | Flag, verbatim | Distinct reason |
|---|---|---|
| `logo-mark.svg`, `logo-mono-black.svg`, `logo-mono-white.svg`, `logo-full.svg` | `OPTICAL: slot edges · derived · the 40-wide slots are lines 20 either side of each diagonal; their meeting points with r 96 / r 72 fall off the grid` | 1 |
| the same four | `OPTICAL: inner ry 72.96 on the ring and the chunk · horizontal strokes read heavier · 24 x 0.96 = 23.04 at the top and bottom tangents; outer edges held` | 2 |
| `logo-stacked.svg` | the same two flags; x coordinates + 128 | 2 |
| `logo-favicon.svg` | `OPTICAL: x 44.86 / 211.14 at y 96, x 72.57 / 183.43 at y 112 and at y 48 · derived · r 96 and r 64 on the 30-degree radii, and the chunk's r 64 cut by the grid line y 48` | 1 |
| `logo-wordmark.svg` | none | — |

**Surviving flags:** 2 distinct reasons on the master against construction.md § Precedence's ceiling of six; 1 on the favicon. **Flags in a separate dark master:** `n/a — no fork`.

### The nine corrections

| # | Correction | Status | How it was answered |
|---|---|---|---|
| 1 | Overshoot | n/a — no flat form shares an alignment edge with the curves | |
| 2 | Size matching | n/a — one form; the chunk is a piece of the ring, not a second shape to match | |
| 3 | Apex centring | n/a — no apex | |
| 4 | Horizontal strokes | 23.04 | 24 × 0.96 at the ring's bottom tangent and the chunk's top tangent; outer edges held on the grid, inner edges moved (precedence rule 2); 24 at the vertical tangents |
| 4 (favicon) | Horizontal strokes | declined — correction 4 would take the favicon's 32 to 30.72 at its horizontal tangents, 1.92 px against 2.00 px at 16 px; the correction is below the resolution of every size this variant renders at | |
| 5 | Perpendicular width | n/a — the slots are negative space, not diagonal ink outlines; their 40 is already measured perpendicular | |
| 6 | Container | n/a — no drawn container; the app-icon tile is composited at export | |
| 7 | Joins | master: every ink corner where a slot edge meets an arc is between 73.87° (r 72: 90° − asin(20/72)) and 106.13°, all above 60°. Favicon: radial ring ends, 90° at all four corners; the chunk's cusps meet its chord at 60°, at the floor. A flat cut at y 96 was drawn first and met the inner circle at 41°; it was replaced by the radial cut. | |
| 8 | Rotation | n/a | |
| 9 | Sidebearings | n/a — no letterform drawn; the type is a `text` run | |

### Stroke and counters

| Field | Record | Source |
|---|---|---|
| Declared weight | 24 (master); 32 (favicon, reproduction.md § The favicon's own spec) | construction.md § Stroke discipline |
| Construction | filled outline, two contours (chunk, ring) | |
| Narrowest ink | 23.04 (horizontal tangents) | |
| Widest ink | 24 | |
| Thick–thin axis | n/a — 1.04×, under the ~2× threshold | |
| Counter count at 256 | 0 enclosed; 1 open space (the ring's interior, its opening and both slots are one connected negative space) | |

| Counter | Family | Narrowest width | Clears | Source |
|---|---|---|---|---|
| Slot between chunk and ring, each side | parallel straight | 62.63 | target 32.72 | 40 + 32 / √2 |
| Ring opening, between the ring's inner ends | parallel straight | 126.42 | target | 191.21 − 64.79 |
| Ring interior | concentric round | 144 | target | 2 × 72 |

### Silhouette

| Field | Record |
|---|---|
| Ink area | 10549.56 (chunk + ring, computed from the exact arc geometry, each arc flattened to 2000 segments) |
| Convex hull area | 33146.12 |
| Ratio | 0.318 |
| Deliberate primitive? | n/a — 0.318 is under reproduction.md § Mono collapse's M4 gate |

### Derivation answers for the anti-slop patterns

| Pattern | Applies? | Test A | Test B |
|---|---|---|---|
| 1 — circle with a gap | the signature does not fire: the ring's outer arc sweeps 360° − 90° − 2 · 12.02° = 245.96°, under the 270° floor, and its gap is centred on 90°, not a diagonal. Recorded anyway, because the mark *is* an interrupted ring | the gap's width is the chunk's own footprint (two slots 40 wide on the 45° and 135° diagonals) and its position is where the chunk was lifted from | close the ring and the chunk has nowhere to have come from: the mark loses its idea. Worse — passes |
| 9 — hexagon container | n/a — no hexagon | | |
| 6 — the lens | n/a — no lens, and no keyword in the brief | | n/a — Test B does not apply to this pattern |
| 8 — the loop | n/a — no equal-radius opposite-sweep pair meeting near the centre, no self-intersecting subpath | | n/a — Test B does not apply to this pattern |

### Type-specific records

| Field | Applies to | Record |
|---|---|---|
| Base sidebearing `s`, container clearance | monogram | n/a — geometric |
| Wordmark model (for `logo-wordmark.svg`) | wordmark variant | single text run |
| Tracking | wordmark variant | letter-spacing = font-size × 0.05 → 47.5 × 0.05 = 2.38 — Minos.NET's `t` |
| Fit | wordmark variant | UNRUN — Bahnschrift did not resolve on the render machine; the sheet's fit row (0.9047, ink edge 244.21) measured the fallback face and is not the wordmark's. font-size 47.5 is an estimate from Bahnschrift's advance widths (≈ 4.04 em for "Rag.NET" at t 0.05, against Minos.NET's measured 37.45 for "Minos.NET"). Decided by re-rendering `docs/design/logo-contact-sheet.html` where Bahnschrift is installed and applying its fit row. |
| Row asymmetry | wordmark variant | UNRUN — the left ink edge depends on the R's sidebearing in the declared face |
| The one custom detail | wordmark variant | declined — as for Minos.NET: at this size the type's stems are a fraction of the mark's weight 24; the lifted chunk carries the detail role in both lockups |
| Rule applications | abstract | n/a — geometric |

### Step 4 — render and critique of the three candidates

Chromium (Playwright 1.58, headless), viewport 1360, served over loopback; `data-sheet-ready` awaited; full-page and `#readout` shots read back. The Playwright MCP's Chrome channel was not installed, so the sheet was driven by `playwright-core` against the cached Chromium — same engine, same template, unmodified.

- **Render 1.** Readout clean for all three (live area inside, `currentColor` only, M3 Δmax ≤ 1/255, no warn or bad rows). Findings: A — C7 / C8, the lift did not read; with four slots the lifted quarter looked like one more segment and the whole read as a crosshair or segmented spinner. B — C8, reads as the digit `2` at every size. C — C8, a common code glyph; reproduction the strongest of the three.
- **Fix, then render 2 (the last).** A redrawn with two slots instead of four, so the ring stays whole and only the chunk is freed; the lifted chunk took correction 4 at its top tangent as well. Render 2: A reads as a ring with one piece lifted out, at every size and on both grounds. Its readout prints `live area — OUTSIDE` on y 16.01 … 240.01: Chromium's arc flattening; the analytic extents are exactly 144 − 96 − 32 = 16 and 144 + 96 = 240. B and C unchanged.
- No binary failures; no withdrawals. C10 `n/a — geometric` for all three.

## Variants

| File | Intended use | `viewBox` | Aspect | Minimum size | Source of the minimum |
|---|---|---|---|---|---|
| `logo-mark.svg` | the mark alone, ≥ 64 px; app-icon tile at export (composited) | `0 0 256 256` | 1:1 | 12 px by formula; 64 px in use | formula: reproduction.md § Minimum sizes — counter `256 × 2 / 62.63` = 8.18 px, ink `256 × 1 / 23.04` = 11.11 px → 12 px; in use, everything ≤ 48 px routes to `logo-favicon.svg` by the exporter's fixed routing, so 64 px is the smallest sheet column the master is used at |
| `logo-full.svg` | horizontal lockup: README header, docs site header | `0 0 864 256` | 3.375:1 | UNRUN | derived — see Lockup measurements; the type side is unmeasured |
| `logo-stacked.svg` | stacked lockup: square-ish placements, cards | `0 0 512 368` | 1.39:1 | UNRUN | derived — see Lockup measurements; the type side is unmeasured |
| `logo-wordmark.svg` | the name alone, and the φ source | `0 0 256 256` | 1:1 | UNRUN | measured off the wordmark's render in Bahnschrift, which did not resolve |
| `logo-mono-black.svg` | one-value use on light grounds | `0 0 256 256` | 1:1 | inherits logo-mark's | the `currentColor` binding — same geometry, `color="#000000"` |
| `logo-mono-white.svg` | one-value use on dark grounds | `0 0 256 256` | 1:1 | inherits; ships ≤ 355 px | same, `color="#ffffff"`; D1 state 1 below 356 px |
| `logo-favicon.svg` | favicon and every raster ≤ 48 px | `0 0 256 256` | 1:1 | 16 px | reproduction.md § The favicon redraw: narrowest counter 55.42, `256 × 2 / 55.42` = 9.24 px; ink 32, `256 / 32` = 8 px; specified for 16 px |

### Lockup measurements

| Field | Record |
|---|---|
| `k` — mark's rendered height ÷ lockup cap height | full: 256 / 96 = 2.67 · stacked: 256 / 64 = 4 — Minos.NET's values |
| Cap-height minimum from the mark side | by formula: 12 / 2.67 = 4.5 px (full), 3 px (stacked); with the mark's in-use minimum of 64 px: **24 px** (full), **16 px** (stacked) |
| `φ_ink` | UNRUN — measured off a render of Bahnschrift 600, which did not resolve on the render machine. Minos.NET measured 0.1445 on the same face, but off Minos.NET's glyphs; "Rag.NET" shares only `.NET`, so that value is not carried over. |
| `φ_ctr` | UNRUN — as above (Minos.NET: 0.1289 off its `s`; the tightest counter here is likely the `a` or `g`) |
| How φ was measured | UNRUN — to be measured as Minos.NET's was: in the sheet's Chromium, on the declared face at 600, glyphs rendered at a 256 px cap height and scanned along fixed rows and columns |
| Cap-height minimum from the type side | UNRUN — `max(1 / φ_ink, 2 / φ_ctr)` once both are measured |
| Lockup `viewBox` width ÷ cap height inside the lockup | full: 864 / 96 = 9 · stacked: 512 / 64 = 8 |
| Lockup width minimum | UNRUN — `max(mark side, type side) × 9` (full), `× 8` (stacked); the type side is unmeasured |
| Lockup's own size status | derived from its components — the mark side from `k`, the type side from the wordmark's `φ`. The lockup is not size-tested on the contact sheet. |
| Composition check (render) | UNRUN — the full lockup's `viewBox` width 864 assumes a run of ≈ 540 units at cap 96; the stacked lockup centres its run with `text-anchor="middle"`, which leaves the ink 2.23 left of centre by the trailing tracking step. Both are decided by rendering the two files with Bahnschrift present. |
| Typeface the `φ` values were measured against | none yet — Bahnschrift, 600, when measured |

### Favicon

| Feature dropped | Why, in reproduction terms |
|---|---|
| the diagonal slots | the master's slot edges are four off-grid lines at 45°; at 16 px a 40-unit slot on a diagonal is 2.5 px across but its edges cross every pixel row at a slant, so both ends of the chunk grey. The favicon cuts the ring on its 30° radii and the chunk on the grid line y 48 instead: two fewer nodes and every cut edge either radial or on the grid |
| the chunk as a quarter-ring | the master's chunk is 23.04 thick at its top, 1.44 px at 16 px; its two inner corners would merge with the ring ends at 20 and 24 px. The favicon's chunk is a solid circular segment 32 deep (2 px at 16 px) |
| the correction-4 thinning | declined — recorded under The nine corrections |

| Field | Record |
|---|---|
| Construction | ring outer r 96 / inner r 64 about (128,144), ends on the 30° radii (outer x 44.86 / 211.14 at y 96, inner x 72.57 / 183.43 at y 112); chunk: circular segment of r 64 about (128,80), top y 16, chord y 48 from x 72.57 to 183.43 — the ring's inner ends, so the opening is the chunk's own width |
| Nodes — favicon / master | 6 / 8 (F2 pass) |
| Counters — favicon / master | 1 / 1 — one open space each; F3 passes on its `counters(master) = 1` clause |
| Subpaths shared with the master | none (F1 pass) |
| Declared weight | 32 |
| Narrowest negative space | 55.42 (chunk to ring end), ≥ 40.96 |
| Ink reaches the live-area bounds on its longer axis | y 16 … 240, graded on the geometry box — filled, the two boxes coincide |
| Silhouette | ink 13240.13 / hull 32460.03 = 0.408 |
| Uniform weight | recorded as a `declined` correction in The nine corrections |
| Favicon critique pass | render 1: favicon in slot A, master in B, wordmark in C; readout clean apart from the arc-flattening `OUTSIDE` (y 15.99), answered analytically. **C1** pass — ring and lid open and distinct at 16 px on both grounds. **C2** pass — holds at 20 / 24. **C3** pass — mono carries it. **C9** — the one row answered above. **C10** n/a — geometric. Before render 2 the correction-7 check above found the 41° join and the ring ends were made radial; render 2 (the Step 7 verification sheet, favicon in slot B) repeats C1–C3 as pass with no new rows. Judgement note: at 48–64 px the solid lid reads heavier than the master's chunk; the favicon is not used there. |

### Raster set

| File | px | Rasterised from | Intended slot | Status |
|---|---|---|---|---|
| `favicon-16.png` | 16 | verdigris-resolved copy of `logo-favicon.svg` (`currentColor` → `#2C8775`) | `<link rel="icon" sizes="16x16">` | written — 16 × 16, corners transparent, ink `#2C8775` |
| `favicon-32.png` | 32 | verdigris-resolved copy of `logo-favicon.svg` | `<link rel="icon" sizes="32x32">` | written — 32 × 32, corners transparent, ink `#2C8775` |
| `favicon-48.png` | 48 | verdigris-resolved copy of `logo-favicon.svg` | legacy browser tab | written — 48 × 48, corners transparent, ink `#2C8775` |
| `favicon.ico` | 16/32/48 | verdigris-resolved copy of `logo-favicon.svg` | site root | written — three frames, 16 / 32 / 48 |
| `apple-touch-icon.png` | 180 | verdigris-resolved copy of `logo-mark.svg` | iOS home screen | written — 180 × 180, corners transparent, ink `#2C8775` |
| `icon-128.png` | 128 | verdigris-resolved copy of `logo-mark.svg` | NuGet package icon (NuGet recommends 128 × 128) | written — 128 × 128; outside the exporter's fixed list, produced with `rsvg-convert -w 128 -h 128`, the converter the exporter selected |
| `icon-192.png` | 192 | verdigris-resolved copy of `logo-mark.svg` | web app manifest; a README logo | written — 192 × 192, corners transparent, ink `#2C8775` |
| `icon-512.png` | 512 | verdigris-resolved copy of `logo-mark.svg` | web app manifest, splash | written — 512 × 512, corners transparent, ink `#2C8775` |
| `icon-1024.png` | 1024 | verdigris-resolved copy of `logo-mark.svg` | app store listing | written — 1024 × 1024, corners transparent, ink `#2C8775` |

Why verdigris-resolved copies, as for Minos.NET: the masters paint in `currentColor`, which has no inherited `color` inside a rasteriser, an `<img>`, a NuGet gallery tile or a home-screen icon, so it renders black. Every raster comes from a temporary copy with `currentColor` replaced by `#2C8775`; the committed SVGs keep `currentColor`. Each PNG was read back: size, transparent top-left corner, first opaque pixel `#2C8775`.

| Field | Record |
|---|---|
| Rasteriser used | librsvg 2.60.0 (`rsvg-convert`), through `export-raster.mjs` (logo-design 1.23.0), exit 0 |
| Sizes at or below 48 px come from the favicon redraw | yes — the exporter's fixed routing; never the master |
| Files written | 8 by the exporter, plus `icon-128.png` with the same converter — 9 |

### Print minimums

`n/a — Q4 named no print or embroidery process`

## Colour

### Binding

`MASTER.md` is absent. The maintainer took the second legal answer — a direction to propose against: "a patina teal at the same luminance as Minos bronze — related family, distinct product". **Proposed, confirmed.** Verdigris is what bronze turns into, and `#2C8775` was tuned to Minos bronze's relative luminance (0.1915 against 0.1897) so the two marks carry the same weight side by side. One value for both grounds, as for Minos.NET. The masters ship in `currentColor` (construction.md § Colour binding); this is the value `color` resolves to at the call site. `ui-design-system` was suggested; it did not block.

| Role | Value | Provenance |
|---|---|---|
| Mark on light | `#2C8775` (verdigris, hue 171°) | proposed against the maintainer's direction, confirmed |
| Mark on dark | `#2C8775` — the same value | as for Minos.NET: one colour for both grounds |
| Accent | n/a — single-value mark | |
| Background pairings tested | `#ffffff` · `#0d1117` (GitHub dark) | |

### Contrast

L(`#2C8775`) = 0.1915 · L(`#ffffff`) = 1 · L(`#0d1117`) = 0.00548.

| Variant | Against | Ratio | Clears 3:1 (WCAG 2.2 SC 1.4.11) |
|---|---|---|---|
| every variant, `color: #2C8775` | `#ffffff` | 1.05 / 0.2415 = **4.35** | yes |
| every variant, `color: #2C8775` | `#0d1117` | 0.2415 / 0.05548 = **4.35** | yes |
| `logo-mono-black.svg` | `#ffffff` | 21.00 | yes |
| `logo-mono-white.svg` | `#0d1117` | 18.93 | yes |

### One-colour print and mono

| Field | Record |
|---|---|
| M1 | yes — every paint in all seven files is `fill="currentColor"`; no literal colour, alpha, blend or gradient |
| M2 | n/a — the mark and favicon are one path each; the lockups pair one path with one `text` whose bounding boxes do not nest |
| M3 | 1/255 on `logo-mark.svg` (11079 ink px) and 0/255 on `logo-favicon.svg` (13746 ink px), from the verification render's readout. `logo-wordmark.svg`: UNRUN — type only, no drawn geometry; its whole-file Δmax 18/255 is glyph rasterisation of a fallback face |
| M4 | recorded in Construction § Silhouette (0.318; favicon 0.408) |
| One-colour print | n/a — not a named context |

### Dark inversion

| Field | Record | Source |
|---|---|---|
| `r` | default — the rate pinned in reproduction.md § r is pinned (3%) | |
| `r · w` | master 0.72 (w 24); favicon 0.96 (w 32) | |
| Fork threshold | master `256 / 0.72` = 355.56 → **356 px**; favicon `256 / 0.96` = 266.67 → 267 px | reproduction.md § When the dark variant forks |
| **D1** | **state 1 — no fork, below the threshold.** `logo-mono-white` is byte-identical to `logo-mono-black` apart from the resolved `color`, and both differ from `logo-mark` only in it. The dark variant is specified for ≤ 355 px; the favicon renders ≤ 48 px against its 267 px threshold. | |
| **D2** | master: slot 62.63 − 0.72 = 61.91, opening 126.42 − 0.72 = 125.70, interior 143.28, all ≥ 32; favicon 55.42 − 0.96 = 54.46 ≥ 40 | |
| Counters drawn at target plus compensation | yes — 62.63 ≥ 32.72 (master), 55.42 ≥ 40.96 (favicon) | |

## Clearspace & minimum sizes

### Clearspace

| Field | Record |
|---|---|
| Largest **enclosed** counter | none — the ring's interior is open through the gap the chunk left, and the slots are open |
| Widest axis of it | n/a — no enclosed counter |
| Rounded up to a whole grid unit | n/a — no enclosed counter |
| Floor applied? | yes — no qualifying enclosed counter |
| **Clearspace** | 64 units = 25 % of the mark's rendered size |
| Datum | the artboard edge, per reproduction.md § Clearspace — never the ink, and the live area's margin is not counted toward it |
| For the lockup | 64 units on all four sides of the lockup's bounding box (the mark renders at its full 256 inside both lockups) — 7.4 % of the full lockup's width, 12.5 % of the stacked lockup's width |

### The counter floor

`n/a — every counter clears the target`

## Misuse

| Do not | What breaks, in this mark's terms |
|---|---|
| Render `logo-mark` at 48 px or below | the chunk is 23.04 units thick — 1.44 px at 16 px — and its inner corners close on the ring ends at 20–24 px. Use `logo-favicon.svg`, which the exporter already routes every size ≤ 48 px to. |
| Close the gap, or set the chunk back into the ring | the mark's idea is the 32-unit lift; at zero lift it is a segmented ring and reads as a spinner (render 1 showed exactly this) |
| Lift the chunk further than 32, or move it sideways | the gap is the chunk's own footprint on the 45° / 135° diagonals; move it and the two stop reading as one shape pulled apart |
| Place ink closer than 64 units per 256 of the mark's size (25 %) | the top opening is 126 units wide; neighbouring ink inside the clearspace reads as a second lifted piece |
| Recolour, add a gradient, a filter or a shadow | the mark is one filled path in one value; anything carried by a second value vanishes on collapse |
| Use it on a ground under 3:1 | `#2C8775` is 4.35 on white and on `#0d1117`; any ground with relative luminance between 0.031 and 0.675 drops below 3:1 against it (0.2415 / 3 − 0.05 and 3 × 0.2415 − 0.05) — check any grey ground |
| Swap verdigris for Minos.NET's bronze, or the reverse | the two marks are siblings by luminance and differ only by hue; one colour for both makes them interchangeable |
| Reset the wordmark or lockups in another face | the tracking, the cap heights and the lockup widths assume Bahnschrift 600; the sheet's fallback-face render ran the wordmark to 244, outside the live area |
| Use the `text`-bearing wordmark or lockups where Bahnschrift is not guaranteed | outline conversion has not been performed — see Production handoff; on GitHub, nuget.org and any Linux machine the face is absent |
| Embed a `currentColor` file through `<img>` | inside `<img>` there is no inherited `color`, so `currentColor` resolves to black — 1.11:1 on `#0d1117`. Inline the SVG, or use `logo-mono-white.svg` or a colour-resolved raster on dark. |
| Show `logo-mono-white` above 355 px | that is D1 state 2 (compensation of 0.72 units reaches a device pixel at 356 px) and this record does not cover it |

## Production handoff

### Outline conversion — not performed

**This skill has no font engine and does not convert type to outlines. That is an explicit non-goal, not an oversight.** Where this mark ships a `text` element, the master resolves against the declared webfont at render time. **Outline conversion has NOT been performed here.** It must happen before the mark is used anywhere the webfont is not guaranteed — print, embroidery, a third party's site, an email client. Until it has, a `text`-bearing wordmark is not a finished asset.

This paragraph ships with the template and is not edited or removed.

### Typeface

| Field | Record |
|---|---|
| Family | Bahnschrift (variable; DIN-derived) — Minos.NET's face |
| Weight | 600 |
| `letter-spacing` | wordmark 2.38 @ 47.5 (estimated size) · full lockup 6.68 @ 133.57 · stacked 4.45 @ 89.04 — `t` = 0.05 in all three |
| Fallback stack | `Bahnschrift, 'DIN Alternate', 'Segoe UI', sans-serif` |
| Resolves on the render machine | **no** — the sheet's readout: "NOT AVAILABLE — "Bahnschrift" did not resolve; this is the fallback face" |
| Licence / where it is hosted | UNRUN — not established; Bahnschrift ships with Windows and is not available on GitHub's or NuGet's renderers |

### Trademark clearance — not performed

**No trademark, design-mark or prior-art clearance has been performed, and nothing in this skill measures collision with an existing mark.** A vision pass over a contact sheet can notice a resemblance it happens to recognise; that is not a search, and it did not run as one here. Clearance is a step for counsel, before the mark is used commercially or filed.

This paragraph ships with the template and is not edited or removed.

### Checks recorded unrun

| Check | Why it could not be run | What would decide it |
|---|---|---|
| Wordmark fit (`logo-wordmark`) | Bahnschrift did not resolve; the sheet measured a fallback face | UNRUN — re-render `docs/design/logo-contact-sheet.html` with Bahnschrift installed and apply its fit row to the font-size |
| `φ_ink`, `φ_ctr` | no render of the declared face | UNRUN — measure off the wordmark at a 256 px cap height in Bahnschrift 600 |
| Cap-height minimum from the type side, both lockup width minimums | depend on φ | UNRUN — compute once φ is measured |
| Lockup composition (`logo-full`, `logo-stacked`) | depends on the run width in the declared face | UNRUN — render both files with Bahnschrift present; set the full lockup's `viewBox` width to the ink edge + 26 as Minos.NET's is, and re-anchor the stacked run on its measured ink |
| `text` element top and bottom extents (`logo-wordmark`, `logo-full`, `logo-stacked`) | `getBBox()` returns the layout box — ascent to descent — not tight ink | UNRUN — outline conversion makes them computable |
| `text` element left extent | the anchor is an origin, not a painted edge | UNRUN — outline conversion |
| `text` element right extent | graded on the harness's `ink edge`, which measured the fallback face (244.21) | UNRUN — the same Bahnschrift render as the fit |
| M3, the type's share | glyph rasterisation is not symmetric under inversion in this engine | UNRUN — the type is not outlined; outline it and M3 decides the whole file |
| Typeface licence and hosting | not established | UNRUN — the face's licence terms for embedding or hosting |
| Stroked containment | | n/a — filled marks, the two boxes coincide |

### Still to do

| Step | Status |
|---|---|
| Outline conversion | **not performed** — see above |
| Trademark clearance | **not performed** — see above |
| Declared face installed where the sheet renders | **no** — open `docs/design/logo-contact-sheet.html` (slot C is the wordmark) in Chromium on a Windows machine, where Bahnschrift ships, and fill the seven type rows: the wordmark fit, `φ_ink`, `φ_ctr`, `k`'s type-side cap minimum, both lockup width minimums, and the lockup composition. `k` itself is already recorded. |
| Icon raster set exported | done — librsvg 2.60, from verdigris-resolved copies; see Raster set |
| Step 8 — wire into the project | done at the maintainer's request — see Project files replaced |

## Asset manifest

| Path | Role | `viewBox` | Nodes | Distinct `OPTICAL:` reasons | Notes |
|---|---|---|---|---|---|
| `assets/brand/logo-mark.svg` | the master | `0 0 256 256` | 8 | 2 | reuse 15/24 = 0.63 |
| `assets/brand/logo-mono-black.svg` | mono, light grounds | `0 0 256 256` | 8 | 2 | `color="#000000"`; otherwise identical to logo-mark (diffed) |
| `assets/brand/logo-mono-white.svg` | mono, dark grounds | `0 0 256 256` | 8 | 2 | `color="#ffffff"`; otherwise identical to logo-mark (diffed) |
| `assets/brand/logo-favicon.svg` | favicon redraw | `0 0 256 256` | 6 | 1 | reuse 9/17 = 0.53 |
| `assets/brand/logo-wordmark.svg` | wordmark | `0 0 256 256` | 0 | 0 | one `text` run, Bahnschrift 600 @ 47.5 (estimated) |
| `assets/brand/logo-full.svg` | horizontal lockup | `0 0 864 256` | 8 | 2 | mark geometry identical to logo-mark; `text` @ 133.57 |
| `assets/brand/logo-stacked.svg` | stacked lockup | `0 0 512 368` | 8 | 2 | mark geometry = logo-mark with x + 128, baked in; `text` @ 89.04 |

| Field | Record |
|---|---|
| Files in the set | 7 |
| Every variant carries `role="img"` and an `aria-label` naming the product | yes, the favicon included — `aria-label="Rag.NET"` on all seven |
| No `width` or `height` on any root `svg` element | yes |
| No empty groups, unreferenced `defs`, or surviving construction geometry | yes — no `g`, no `defs`; one path and at most one `text` per file |
| Mono variants diffed against their source | identical apart from the root `color` attribute |
| Every variant's ink sits inside the live area | mark and favicon x 32 … 224 / y 16 … 240, graded on the geometry box (filled; the boxes coincide); wordmark: UNRUN — its right edge in the declared face is unmeasured, and in the fallback face it reached 244; lockups are non-square and checked for composition only |
| Raster files recorded in Raster files | yes — all nine |

### Raster files

| Path | px | Rasterised from | Bytes |
|---|---|---|---|
| `assets/brand/favicon-16.png` | 16 | verdigris-resolved copy of `logo-favicon.svg` (`currentColor` → `#2C8775`) | 415 |
| `assets/brand/favicon-32.png` | 32 | verdigris-resolved copy of `logo-favicon.svg` | 760 |
| `assets/brand/favicon-48.png` | 48 | verdigris-resolved copy of `logo-favicon.svg` | 1166 |
| `assets/brand/favicon.ico` | 16/32/48 | verdigris-resolved copy of `logo-favicon.svg` | 2395 |
| `assets/brand/apple-touch-icon.png` | 180 | verdigris-resolved copy of `logo-mark.svg` (`currentColor` → `#2C8775`) | 4789 |
| `assets/brand/icon-128.png` | 128 | verdigris-resolved copy of `logo-mark.svg` | 3384 |
| `assets/brand/icon-192.png` | 192 | verdigris-resolved copy of `logo-mark.svg` | 5115 |
| `assets/brand/icon-512.png` | 512 | verdigris-resolved copy of `logo-mark.svg` | 14632 |
| `assets/brand/icon-1024.png` | 1024 | verdigris-resolved copy of `logo-mark.svg` | 31462 |

| Field | Record |
|---|---|
| Files in the raster set | 9 |
| Every raster's source appears in the SVG manifest above | yes — `logo-mark.svg` and `logo-favicon.svg`, each through a verdigris-resolved temporary copy that is not committed |
| Rasteriser used | recorded once in Raster set |

### Project files replaced

Nothing was replaced: the repository had no icon to replace. Every slot below was **added** at the maintainer's explicit request, which goes beyond SKILL.md Step 8's replace-only default; each addition is listed so it can be reverted on its own.

| Path | Change | Source |
|---|---|---|
| `Directory.Build.props` | `PackageIcon` = `icon.png`, and an item packing `assets/brand/icon-128.png` as `icon.png` into every package; the comment that said "No PackageIcon: the repository has no icon asset" now names the raster | `icon-128.png` |
| `tests/Rag.NET.PackageValidation.Tests/ProducedPackageTests.cs` | `EveryPackageCarriesTheBrandIcon`: every produced package names `icon.png` and carries it byte-identical to `assets/brand/icon-128.png` | — |
| `README.md` | `![Rag.NET](assets/brand/icon-192.png)` under the title — a verdigris raster reads on GitHub light and dark (4.35:1 on both) | `icon-192.png` |
| `static/img/favicon.ico` | new; `docusaurus.config.ts` already named `img/favicon.ico`, which did not exist | copy of `assets/brand/favicon.ico` |
| `static/img/logo.svg` | new; the docs navbar logo, wired as `navbar.logo` | verdigris-resolved copy of `logo-mark.svg` (`currentColor` → `#2C8775`), because the navbar shows it in an `<img>` |

| Field | Record |
|---|---|
| Files replaced | none — five files changed or added, above |
| Every replaced path was tracked and clean before the write | n/a — nothing was replaced; `Directory.Build.props`, `README.md`, `docusaurus.config.ts` and `ProducedPackageTests.cs` were tracked and clean at `fb25440a` |
| Slots found but skipped | none |
| Verified | `dotnet pack` of the solution produced 73 packages, each with `icon.png` (3384 bytes); `Rag.NET.PackageValidation.Tests` 34 / 34 and `Rag.NET.RepoConventions.Tests` 129 / 129 pass; `npm run build` emits `build/img/favicon.ico` and `build/img/logo.svg` |

## Structural self-verification (Step 7 item 3)

Run against `assets/brand/` after the raster export.

- [x] Every file in the Asset manifest exists at its path, and every SVG in the directory is in the manifest (7 and 7).
- [x] Each SVG parses (XML, root `{http://www.w3.org/2000/svg}svg`).
- [x] Each root `svg` carries `xmlns="http://www.w3.org/2000/svg"` — string check, all seven.
- [x] Each root carries a `viewBox`; the five square variants all carry `0 0 256 256`; the lockups declare `0 0 864 256` and `0 0 512 368`.
- [x] No `filter` element anywhere.
- [x] The mono variants bind every paint to `currentColor` and differ from their source only in the resolved `color`.
- [x] Every file the exporter reported written (8), and `icon-128.png`, exists in `assets/brand/` and has a row in Raster files naming the SVG it was rasterised from.
- [x] No raster file in the asset directory lacks a manifest row (9 on disk, 9 rows).
