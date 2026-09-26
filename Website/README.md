# TOR POS Website

This folder is the source snapshot of the deployed TOR POS private preview.

- AppDeploy app: `tor-pos-private-preview-b64g9z`
- Source version: `1789949121991`
- Preview: https://tor-pos-private-preview-b64g9z.v2.appdeploy.ai/
- Git branch: `website-source`

## Local development

```bash
cd Website
npm install
npm run dev
```

`npm run dev`, `npm run build` and `npm run preview` first recreate
`public/resources/tor-pos-logo.jpg` from the lossless base64 source file
`public/resources/tor-pos-logo.jpg.b64`.

## Claude handoff

Work only in `Website/` for website changes unless a change explicitly requires the
shared TOR POS contract. Do not modify Desktop/Cloud fiscal code as part of website work.

The simulator may consume `Shared/simulator-contract.json` from the TOR-POS repository.
Keep the embedded simulator fallback functional if that shared contract cannot be fetched.

The website is currently a test/private-preview build and must not imply that it accepts
real sales, orders, contracts, or paid services until the owner explicitly changes that policy.

No .env files, API keys, passwords, tokens, or deployment secrets are stored in this folder.
