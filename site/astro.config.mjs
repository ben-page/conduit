// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import starlightLinksValidator from 'starlight-links-validator';
import { base, editBaseUrl, repository, sidebar, site } from './site.config.mjs';

// Links in docs/ known to point at no heading, tolerated so the site builds until they are fixed at the source.
// Each entry is a page slug and the link exactly as written. Fix the link in docs/, then delete its entry here.
const knownBrokenLinks = [
    // docs/security.md: the heading is "IInboundInterceptor", so its anchor is #iinboundinterceptor.
    { slug: 'security', link: '#iwireinterceptor' },
];

// The pages come from the repository's docs/: scripts/prepare-docs.mjs copies them into src/content/docs/ before
// every dev and build run. See README.md.
export default defineConfig({
    site,
    base,
    integrations: [
        starlight({
            title: 'Conduit',
            description:
                'A source-generated mediator for .NET 10, with a typed TypeScript client and transports for HTTP, SignalR and four message brokers.',
            social: [{ icon: 'github', label: 'GitHub', href: repository }],
            // Each page also states its own editUrl (see site.config.mjs), which takes precedence.
            editLink: { baseUrl: editBaseUrl },
            // The pages are copies made at build time, so git history cannot date them.
            lastUpdated: false,
            // prepare-docs.mjs writes the 404 page as a content entry, built to 404.html like any other page.
            // Starlight's injected /404 route would then collide with it, and Astro warns on every build.
            disable404Route: true,
            sidebar,
            plugins: [
                // Fails the build on a broken internal link or heading anchor. Every check is on: a relative link is
                // an error (prepare-docs.mjs writes each one as an absolute URL under the base), and so is a link
                // that spells out the site's own origin.
                starlightLinksValidator({
                    errorOnFallbackPages: true,
                    errorOnInconsistentLocale: true,
                    errorOnRelativeLinks: true,
                    errorOnInvalidHashes: true,
                    errorOnLocalLinks: true,
                    sameSitePolicy: 'error',
                    // The validator's slug carries the base ("conduit/audit").
                    exclude: ({ slug, link }) =>
                        knownBrokenLinks.some(
                            (known) => `${base.slice(1)}/${known.slug}` === slug && known.link === link,
                        ),
                }),
            ],
        }),
    ],
});
