import { themes as prismThemes } from 'prism-react-renderer';
import type { Config } from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

const config: Config = {
  title: 'Rag.NET',
  tagline: 'A modular RAG pipeline library for .NET built on Microsoft.Extensions.AI',
  favicon: 'img/favicon.ico',

  // Points at the account that actually owns the repository. It read `rag-net` until the site was
  // deployed: the RAG-Net organisation exists but owns no repositories, so nothing could publish
  // from it, and the 2026-08-08 design left the value alone rather than half-correct it —
  // "changing it without knowing the answer would replace an obviously wrong value with a plausibly
  // wrong one". The answer is now chosen, so these match where the code is.
  //
  // organizationName and projectName decide the deployment target; url and baseUrl decide the
  // absolute links the built site emits. All four have to agree or the site builds clean and links
  // off-site.
  url: 'https://marcelroozekrans.github.io',
  baseUrl: '/Rag.NET/',

  organizationName: 'MarcelRoozekrans',
  projectName: 'Rag.NET',

  onBrokenLinks: 'throw',
  onBrokenMarkdownLinks: 'warn',

  markdown: {
    mermaid: true,
  },

  themes: ['@docusaurus/theme-mermaid'],

  presets: [
    [
      'classic',
      {
        docs: {
          routeBasePath: '/',
          sidebarPath: './sidebars.ts',
          // Was github.com/rag-net/... — the same stale organisation the url and organizationName
          // carried. It is worth calling out separately because it fails differently: those two
          // break the site's own absolute links, which is loud, while this one renders an "Edit
          // this page" control on EVERY page that 404s, which nobody notices until a contributor
          // clicks it.
          editUrl: 'https://github.com/MarcelRoozekrans/Rag.NET/edit/main/docs/',

          // `plans/**` was already excluded; `planning/**` was not, and the two are the same kind
          // of thing. Without it the site publishes ROADMAP, STATE, CONVENTIONS, MILESTONE and the
          // five milestone backlogs as pages beside the guide — internal working state presented as
          // product documentation. They stay readable in the repository, where they belong.
          //
          // The pre-push-review pattern is belt and braces: those artefacts are removed and
          // git-ignored, but a local build still sees any that are sitting untracked in the working
          // tree, and `npm run build` should agree with what CI publishes.
          exclude: ['plans/**', 'planning/**', 'pre-push-review-*.md'],
        },
        blog: false,
        theme: {
          customCss: './src/css/custom.css',
        },
      } satisfies Preset.Options,
    ],
  ],

  themeConfig: {
    navbar: {
      title: 'Rag.NET',
      items: [
        {
          type: 'docSidebar',
          sidebarId: 'guideSidebar',
          position: 'left',
          label: 'Docs',
        },
        {
          href: 'https://github.com/MarcelRoozekrans/Rag.NET',
          label: 'GitHub',
          position: 'right',
        },
      ],
    },
    footer: {
      style: 'dark',
      links: [
        {
          title: 'Docs',
          items: [
            { label: 'Getting Started', to: '/getting-started' },
            { label: 'Architecture', to: '/guide/architecture' },
          ],
        },
        {
          title: 'More',
          items: [
            {
              label: 'GitHub',
              href: 'https://github.com/MarcelRoozekrans/Rag.NET',
            },
          ],
        },
      ],
      copyright: `Copyright © ${new Date().getFullYear()} Rag.NET. Built with Docusaurus.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
      additionalLanguages: ['csharp', 'bash', 'json', 'yaml'],
    },
    mermaid: {
      theme: { light: 'neutral', dark: 'dark' },
    },
  } satisfies Preset.ThemeConfig,
};

export default config;
