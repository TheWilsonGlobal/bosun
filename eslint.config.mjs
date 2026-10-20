// Root ESLint config: judges code quality of the repo-level scripts only.
// Layout is Prettier's job (eslint-config-prettier switches the conflicting rules off).
// The SPAs (apps/frontend, apps/admin) carry their own flat config and are linted via `pnpm -r lint`.
import js from '@eslint/js';
import globals from 'globals';
import prettier from 'eslint-config-prettier';

export default [
  { ignores: ['apps/**', '**/bin/**', '**/obj/**', 'data/**', 'node_modules/**', 'dist/**'] },
  js.configs.recommended,
  {
    files: ['**/*.{js,mjs,cjs}'],
    languageOptions: { ecmaVersion: 2024, sourceType: 'module', globals: { ...globals.node } },
  },
  prettier,
];
