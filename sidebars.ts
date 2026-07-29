import type { SidebarsConfig } from '@docusaurus/plugin-content-docs';

const sidebars: SidebarsConfig = {
  guideSidebar: [
    'index',
    'why-rag',
    'getting-started',
    {
      type: 'category',
      label: 'Guide',
      items: [
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
        'reference/ci',
        'reference/features',
      ],
    },
  ],
};

export default sidebars;
