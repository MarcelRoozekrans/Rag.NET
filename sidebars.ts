import type { SidebarsConfig } from '@docusaurus/plugin-content-docs';

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
        'guide/evaluation',
        'guide/observability',
        'guide/diagnostics',
        'guide/extending',
        'guide/mcp',
      ],
    },
    {
      type: 'category',
      label: 'Reference',
      items: [
        'reference/benchmarks',
        'reference/retrieval-quality',
        'reference/library-comparison',
        'reference/library-comparison-scope',
        'reference/ci',
        'reference/features',
      ],
    },
  ],
};

export default sidebars;
