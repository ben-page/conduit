// @ts-check
// The site's address, its repository, and its sidebar. astro.config.mjs builds the site from these, and
// scripts/prepare-docs.mjs uses them to rewrite links and to check that every page is in the sidebar.

/** The GitHub Pages origin. */
export const site = 'https://ben-page.github.io';

/** The project path GitHub Pages serves the site under. */
export const base = '/conduit';

/** The repository the docs live in: edit links, and links to files outside docs/, point here. */
export const repository = 'https://github.com/ben-page/conduit';

/** The branch edit links and repository links name. */
export const branch = 'main';

/**
 * Where a page's source is edited: the page's docs/-relative path is appended. Starlight would append the copy's
 * path instead (src/content/docs/...), which is not in the repository, so prepare-docs.mjs gives every page its own
 * `editUrl` built from this.
 */
export const editBaseUrl = `${repository}/edit/${branch}/docs/`;

/**
 * The landing page. docs/why-conduit.md is copied to the content root as index.md, so it is served at the site
 * root, and links to it are rewritten there.
 */
export const landing = 'why-conduit.md';

/**
 * The sidebar, in the README's reading order. Every page the prepare script copies must appear here exactly once as
 * a `slug` (or, for the landing, as the link to `/`); prepare-docs.mjs fails the build otherwise.
 *
 * @type {NonNullable<import('@astrojs/starlight/types').StarlightUserConfig['sidebar']>}
 */
export const sidebar = [
    {
        label: 'Learn',
        items: [
            { label: 'Why Conduit', link: '/' },
            { label: 'Getting Started', slug: 'getting-started' },
            { label: 'Core Concepts', slug: 'core-concepts' },
            { label: 'Pipeline Behaviors', slug: 'pipeline-behaviors' },
            { label: 'Error Handling', slug: 'error-handling' },
            { label: 'Modular Monolith', slug: 'modular-monolith' },
            { label: 'Dispatch Flows', slug: 'dispatch-flows' },
        ],
    },
    {
        label: 'Connect',
        items: [
            { label: 'Transports', slug: 'transports' },
            { label: 'SignalR', slug: 'transports/signalr' },
            { label: 'TypeScript Client', slug: 'transports/typescript-client' },
            { label: 'HTTP', slug: 'transports/http' },
            { label: 'Azure Service Bus', slug: 'transports/azure-service-bus' },
            { label: 'RabbitMQ', slug: 'transports/rabbitmq' },
            { label: 'Amazon SQS', slug: 'transports/amazon-sqs' },
            { label: 'ActiveMQ', slug: 'transports/activemq' },
            { label: 'Security', slug: 'security' },
        ],
    },
    {
        label: 'Make it durable',
        items: [
            { label: 'Persistence', slug: 'persistence' },
            { label: 'Outbox', slug: 'outbox' },
            { label: 'Inbox', slug: 'inbox' },
            { label: 'Dead-Letter Queue', slug: 'dead-letter-queue' },
            { label: 'Background Jobs', slug: 'jobs' },
            { label: 'Audit Trail', slug: 'audit' },
            { label: 'Dispatch Retry', slug: 'dispatch-retry' },
            // A section of Pipeline Behaviors, as in the README, not a page of its own.
            { label: 'Resilience', link: '/pipeline-behaviors/#resilience-polly' },
        ],
    },
    {
        label: 'Reference',
        items: [
            { label: 'Configuration Reference', slug: 'configuration-reference' },
            { label: 'Diagnostics', slug: 'diagnostics' },
            { label: 'Glossary', slug: 'glossary' },
            { label: 'FAQ', slug: 'faq' },
        ],
    },
];
