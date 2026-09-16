import type { SidebarsConfig } from '@docusaurus/plugin-content-docs';

/**
 * Every published page belongs to exactly one category here.
 *
 * Docusaurus generates a route for any file under `docs/` whether or not the sidebar names it, so
 * a page left out of this file is built, deployed and reachable by URL while being invisible to
 * anyone browsing. Thirteen pages were in that state — 4,751 of the site's 17,500 lines, including
 * the whole of RAPTOR, GraphRAG, security, resilience, conversational memory and every SaaS
 * connector. `guide/raptor` and `guide/graphrag` had no inbound link from any listed page either,
 * so browsing could not reach them at all.
 *
 * `DocumentationSidebarTests` fails the build if a page under `docs/` is ever again missing from
 * this file, because "the sidebar is complete" is not a property a green docs build can observe:
 * the site builds clean either way. Excluded trees (`plans/`, `planning/`, `pre-push-review-*`)
 * are declared in `docusaurus.config.ts` and the guard reads them from there rather than keeping
 * its own copy.
 */
const sidebars: SidebarsConfig = {
  guideSidebar: [
    'index',
    'why-rag',
    'getting-started',
    'positioning',
    {
      type: 'category',
      label: 'Guide',
      items: [
        'guide/choosing-packages',
        'guide/architecture',
        'guide/ingestion',
        'guide/chunking',
        'guide/retrieval',
        'guide/post-retrieval',
        'guide/vector-stores',
      ],
    },
    {
      // The techniques that sit on top of a working pipeline rather than inside it: each is an
      // opt-in package with its own builder call, and none of them was reachable from the nav.
      type: 'category',
      label: 'Advanced Retrieval',
      items: [
        'guide/raptor',
        'guide/graphrag',
        'query-techniques',
        'answer-engines',
        'guide/memory',
      ],
    },
    {
      type: 'category',
      label: 'Sources',
      items: ['guide/data-providers'],
    },
    {
      type: 'category',
      label: 'Production',
      items: [
        'guide/security',
        'guide/resilience',
        'guide/observability',
        'guide/diagnostics',
        'guide/evaluation',
        'guide/shadow-mode',
      ],
    },
    {
      type: 'category',
      label: 'Integration',
      items: ['guide/mcp', 'guide/mediator', 'guide/extending'],
    },
    {
      type: 'category',
      label: 'Reference',
      items: [
        'reference/features',
        'reference/benchmarks',
        'reference/retrieval-quality',
        'reference/library-comparison',
        'reference/library-comparison-scope',
        'reference/library-comparison-defaults',
        'reference/opentelemetry',
        'reference/oss-libraries',
        'reference/ci',
      ],
    },
  ],
};

export default sidebars;
