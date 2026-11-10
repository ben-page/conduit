import { defineCollection } from 'astro:content';
import { docsLoader, i18nLoader } from '@astrojs/starlight/loaders';
import { docsSchema, i18nSchema } from '@astrojs/starlight/schema';

export const collections = {
    // src/content/docs/ is generated from ../docs by scripts/prepare-docs.mjs; see README.md.
    docs: defineCollection({ loader: docsLoader(), schema: docsSchema() }),
    // Starlight UI-string overrides: src/content/i18n/en.json overrides none, but its presence keeps Astro from
    // warning on every build that the collection Starlight reads is missing.
    i18n: defineCollection({ loader: i18nLoader(), schema: i18nSchema() }),
};
