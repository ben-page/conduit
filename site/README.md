# Documentation site

The Conduit documentation site: [Astro](https://astro.build) with the [Starlight](https://starlight.astro.build)
theme, published to GitHub Pages by `.github/workflows/docs.yml` on every push to `main` that touches the docs.

The content is not here. It is the Markdown in the repository's `docs/`, which stays the source and reads the same on
GitHub. Before every `dev` and `build` run, `scripts/prepare-docs.mjs` copies it into `src/content/docs/`
(git-ignored), taking each page's title from its first heading, dropping the hand-written "Previous | Home | Next"
footer, and rewriting relative links for the site. `docs/why-conduit.md` becomes the landing page. `docs/contributing/`
and `docs/history/` are left out. Edit the files in `docs/`, never the copies.

## Run it locally

Node.js 22.23.4 or later (see `.nvmrc`; Astro's minimum, and the floor [CONTRIBUTING.md](../CONTRIBUTING.md) states):

```shell
cd site
npm ci
npm run dev        # http://localhost:4321/conduit/
```

`npm run dev` copies `docs/` once, at startup. After editing a guide, run `npm run prepare-docs` (the dev server picks
up the new copy) or restart it.

```shell
npm run build      # the site, into dist/; fails on a broken link or heading anchor
npm run preview    # serve dist/ at http://localhost:4321/conduit/
```

## Adding a guide

The sidebar is listed by hand in `site.config.mjs`, in the README's reading order. A new page under `docs/` must be
added there, and the build fails until it is: `prepare-docs.mjs` checks that every copied page appears in the sidebar
exactly once. A guide that is not for the site belongs in the script's `excluded` set instead.
