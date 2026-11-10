// @ts-check
// Copies the repository's docs/ into src/content/docs/ (git-ignored) as Starlight pages. docs/ stays plain Markdown
// that reads well on GitHub; this script makes the few changes the site needs:
//
//   - the first "# Heading" becomes the page's `title:` frontmatter and is removed (Starlight renders the title);
//   - the hand-written "Previous | Home | Next" footer, and the `---` rule above it, are removed (Starlight
//     generates prev/next links from the sidebar);
//   - relative links are rewritten: a link to a copied page becomes that page's URL under the site's base, a link to
//     the README becomes the site root, and a link to any other file in the repository points at it on GitHub;
//   - each page's `editUrl` names its source in docs/, so "Edit page" opens the file the page was copied from;
//   - docs/why-conduit.md is copied to the content root as index.md, the landing page;
//   - a 404 page is written alongside.
//
// docs/design.md, docs/transport-test-matrices.md and docs/superpowers/ are maintainer documents and are left out.
// The script fails, and the build with it, on a link to a file that does not exist, a page with no "# Heading", or a
// page the sidebar in site.config.mjs does not list exactly once.
//
// Run by `npm run dev` and `npm run build` (predev/prebuild), or on its own with `npm run prepare-docs`.

import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { base, branch, editBaseUrl, landing, repository, sidebar } from '../site.config.mjs';

const siteDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = path.resolve(siteDir, '..');
const docsDir = path.join(repoRoot, 'docs');
const outDir = path.join(siteDir, 'src', 'content', 'docs');

/** Paths under docs/ that are not part of the site. */
const excluded = new Set(['design.md', 'transport-test-matrices.md', 'superpowers']);

/** "Previous: [A](a.md) | [Home](../README.md) | Next: [B](b.md)", either side optional. */
const footerPattern =
    /^(?:Previous: \[[^\]]+\]\([^)\s]+\) \| )?\[Home\]\((?:\.\.\/)*README\.md\)(?: \| Next: \[[^\]]+\]\([^)\s]+\))?\s*$/;
const rulePattern = /^(?:-{3,}|\*{3,}|_{3,})\s*$/;
const headingPattern = /^#\s+(.+?)(?:\s+#+)?\s*$/;
const fencePattern = /^\s*(`{3,}|~{3,})/;
const inlineLinkPattern = /\]\(([^)\s]+)((?:\s+"[^"]*")?)\)/g;
const protocolOrRootPattern = /^(?:[a-z][a-z0-9+.-]*:|\/|#)/i;

/** @param {string} directory @param {string} relative @returns {string[]} docs/-relative POSIX paths */
function collectPages(directory, relative = '') {
    const pages = [];
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
        const entryRelative = relative ? `${relative}/${entry.name}` : entry.name;
        if (excluded.has(entryRelative)) {
            continue;
        }
        if (entry.isDirectory()) {
            pages.push(...collectPages(path.join(directory, entry.name), entryRelative));
        } else if (entry.isFile() && entry.name.endsWith('.md')) {
            pages.push(entryRelative);
        }
    }
    return pages.sort();
}

/** @param {string} page docs/-relative path @returns {string} the Starlight slug ('' for the landing) */
function slugFor(page) {
    if (page === landing) {
        return '';
    }
    const withoutExtension = page.slice(0, -'.md'.length);
    if (withoutExtension === 'index') {
        return '';
    }
    return withoutExtension.endsWith('/index') ? withoutExtension.slice(0, -'/index'.length) : withoutExtension;
}

/** @param {string} slug */
function urlFor(slug) {
    return slug ? `${base}/${slug}/` : `${base}/`;
}

/** @param {string[]} lines @returns {boolean[]} true for each line inside (or fencing) a code block */
function codeLineMask(lines) {
    const mask = [];
    /** @type {{ char: string, length: number } | null} */
    let fence = null;
    for (const line of lines) {
        const match = fencePattern.exec(line);
        if (fence === null) {
            if (match) {
                fence = { char: match[1][0], length: match[1].length };
            }
            mask.push(match !== null);
        } else {
            mask.push(true);
            if (match && match[1][0] === fence.char && match[1].length >= fence.length && line.trim() === match[1]) {
                fence = null;
            }
        }
    }
    return mask;
}

/**
 * @param {string} target the link target as written
 * @param {string} page the docs/-relative page the link is on
 * @param {Map<string, string>} slugs copied page → slug
 */
function rewriteTarget(target, page, slugs) {
    if (protocolOrRootPattern.test(target)) {
        return target;
    }
    const hashIndex = target.indexOf('#');
    const targetPath = hashIndex < 0 ? target : target.slice(0, hashIndex);
    const hash = hashIndex < 0 ? '' : target.slice(hashIndex);
    const repoPath = path.posix.normalize(path.posix.join('docs', path.posix.dirname(page), decodeURI(targetPath)));
    if (repoPath.startsWith('../')) {
        throw new Error(`docs/${page}: the link "${target}" points outside the repository.`);
    }
    const docsPath = repoPath.startsWith('docs/') ? repoPath.slice('docs/'.length) : null;
    if (docsPath !== null && slugs.has(docsPath)) {
        return urlFor(/** @type {string} */ (slugs.get(docsPath))) + hash;
    }
    if (repoPath === 'README.md' && hash === '') {
        return urlFor('');
    }
    const absolute = path.join(repoRoot, repoPath);
    if (!existsSync(absolute)) {
        throw new Error(`docs/${page}: the link "${target}" points at ${repoPath}, which does not exist.`);
    }
    const kind = statSync(absolute).isDirectory() ? 'tree' : 'blob';
    return `${repository}/${kind}/${branch}/${repoPath === '.' ? '' : repoPath}${hash}`;
}

/** @param {string} line @param {string} page @param {Map<string, string>} slugs */
function rewriteLinks(line, page, slugs) {
    /** @param {string} _ @param {string} target @param {string} title */
    const rewrite = (_, target, title) => `](${rewriteTarget(target, page, slugs)}${title})`;
    // Odd parts are inline code spans, left as written.
    return line
        .split(/(`+[^`]*?`+)/)
        .map((part, index) => (index % 2 === 1 ? part : part.replace(inlineLinkPattern, rewrite)))
        .join('');
}

/** @param {string} source @param {string} page @param {Map<string, string>} slugs */
function transform(source, page, slugs) {
    let lines = source.replace(/\r\n?/g, '\n').split('\n');

    /** @type {string[]} */
    let frontmatter = [];
    if (lines[0] === '---') {
        const end = lines.indexOf('---', 1);
        if (end > 0) {
            frontmatter = lines.slice(1, end);
            lines = lines.slice(end + 1);
        }
    }
    const mask = codeLineMask(lines);

    // The hand-written footer: the last non-blank line, and the rule above it.
    let end = lines.length;
    const skipBlankLines = () => {
        while (end > 0 && lines[end - 1].trim() === '') {
            end--;
        }
    };
    skipBlankLines();
    if (end > 0 && !mask[end - 1] && footerPattern.test(lines[end - 1])) {
        end--;
        skipBlankLines();
        if (end > 0 && !mask[end - 1] && rulePattern.test(lines[end - 1])) {
            end--;
            skipBlankLines();
        }
    }
    lines = lines.slice(0, end);

    // The title, from the first heading, unless the page already states one.
    let title = null;
    let headingIndex = -1;
    if (!frontmatter.some((line) => /^title\s*:/.test(line))) {
        headingIndex = lines.findIndex((line, index) => !mask[index] && headingPattern.test(line));
        if (headingIndex < 0) {
            throw new Error(`docs/${page}: no "# Heading" to take the page title from.`);
        }
        title = /** @type {RegExpExecArray} */ (headingPattern.exec(lines[headingIndex]))[1].replaceAll('`', '');
    }

    lines = lines.map((line, index) => (mask[index] ? line : rewriteLinks(line, page, slugs)));

    if (headingIndex >= 0) {
        const removeCount = lines[headingIndex + 1]?.trim() === '' ? 2 : 1;
        lines.splice(headingIndex, removeCount);
    }

    const header = [`# Generated from docs/${page} by site/scripts/prepare-docs.mjs. Edit the source, not this copy.`];
    if (title !== null) {
        header.push(`title: ${JSON.stringify(title)}`);
    }
    if (!frontmatter.some((line) => /^editUrl\s*:/.test(line))) {
        header.push(`editUrl: ${JSON.stringify(editBaseUrl + page)}`);
    }
    return ['---', ...header, ...frontmatter, '---', '', ...lines, ''].join('\n');
}

/** @returns {{ slugs: string[], links: string[] }} the sidebar's page slugs (the landing as '') and other links */
function sidebarTargets() {
    const slugs = [];
    const links = [];
    /** @param {readonly any[]} items */
    const visit = (items) => {
        for (const item of items) {
            if (typeof item === 'string') {
                slugs.push(item);
            } else if ('slug' in item) {
                slugs.push(item.slug);
            } else if ('link' in item) {
                if (item.link === '/') {
                    slugs.push('');
                } else {
                    links.push(item.link);
                }
            } else if ('items' in item) {
                visit(item.items);
            }
        }
    };
    visit(sidebar);
    return { slugs, links };
}

/** @param {Map<string, string>} slugs copied page → slug */
function checkSidebar(slugs) {
    const pageSlugs = new Set(slugs.values());
    const { slugs: listed, links } = sidebarTargets();
    const problems = [];
    for (const slug of pageSlugs) {
        const count = listed.filter((item) => item === slug).length;
        if (count !== 1) {
            problems.push(`the page "${slug || '/'}" is listed ${count} times (expected once)`);
        }
    }
    for (const slug of listed) {
        if (!pageSlugs.has(slug)) {
            problems.push(`the sidebar lists "${slug}", which is not a page`);
        }
    }
    for (const link of links) {
        if (link.startsWith('/')) {
            const slug = link.split('#')[0].replace(/^\/+|\/+$/g, '');
            if (!pageSlugs.has(slug)) {
                problems.push(`the sidebar link "${link}" is not a page`);
            }
        }
    }
    if (problems.length > 0) {
        throw new Error(`site.config.mjs sidebar does not match docs/:\n  - ${problems.join('\n  - ')}`);
    }
}

/** Copies docs/ into src/content/docs/. */
function main() {
    const pages = collectPages(docsDir);
    if (!pages.includes(landing)) {
        throw new Error(`docs/${landing}, the landing page, does not exist.`);
    }
    const slugs = new Map(pages.map((page) => [page, slugFor(page)]));
    const seen = new Map();
    for (const [page, slug] of slugs) {
        if (seen.has(slug)) {
            throw new Error(`docs/${page} and docs/${seen.get(slug)} would both be served at ${urlFor(slug)}.`);
        }
        seen.set(slug, page);
    }
    checkSidebar(slugs);

    rmSync(outDir, { recursive: true, force: true });
    for (const page of pages) {
        const output = path.join(outDir, page === landing ? 'index.md' : page);
        mkdirSync(path.dirname(output), { recursive: true });
        writeFileSync(output, transform(readFileSync(path.join(docsDir, page), 'utf8'), page, slugs));
    }

    // The 404 page, as a content entry built to 404.html (GitHub Pages serves it for any missing path). It mirrors
    // Starlight's built-in one, which astro.config.mjs turns off: that one looks for this entry and, with Astro 7,
    // warns on every build when it is missing and collides with it when it is not.
    writeFileSync(
        path.join(outDir, '404.md'),
        [
            '---',
            '# Generated by site/scripts/prepare-docs.mjs.',
            "title: '404'",
            'template: splash',
            'editUrl: false',
            'pagefind: false',
            'hero:',
            '  tagline: Page not found. Check the URL or try using the search bar.',
            '---',
            '',
            `Or start from [Why Conduit](${urlFor('')}).`,
            '',
        ].join('\n'),
    );
    console.log(`prepare-docs: ${pages.length} pages from docs/ to ${path.relative(siteDir, outDir)}/`);
}

// Run when invoked (`node scripts/prepare-docs.mjs`); importing the module, to exercise transform(), runs nothing.
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    main();
}

export { transform };
