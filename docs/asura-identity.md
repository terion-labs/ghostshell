# Asura identity

The product name is **Asura**, its website is `https://asura.sh/`, and its
repository target is `https://github.com/terion-labs/asura`.

Use `Asura` for .NET namespaces, assemblies, classes, and display text;
`asura` for commands, filenames, protocol names, and lowercase identifiers;
and `ASURA` for environment variables and build symbols. The bundle,
update-package, and vault-service identity is `sh.asura`. Java packages
start with `sh.asura`. Development identities retain their separate
`.development` suffix and `Asura Development` data directory.

Asura starts with fresh application storage. It does not read, migrate,
rename, or delete data or credentials from the previous product. Its
database is `asura.db`. Native payloads must be rebuilt because application
ABI exports, worker names, receipts, and hashes changed with the rename.
Schema 10 now constrains multiplexer session names to `asura-*`. Its frozen
fixture checksum pins that exact SQL for the separate Asura storage lineage;
the migration checksum checks and upgrade/rollback tests remain in force.

The repository owner will rename the GitHub repository and switch GitHub
Pages and DNS to `asura.sh` separately. Source links and release downloads
already target the new repository. These links require that repository
rename, and download links require a release containing the Asura artifacts.

`docs/acceptance/linux-arm64-xvfb/` and `docs/acceptance/platform-vault/`
preserve historical logs, screenshots, package hashes, and source fingerprints
byte for byte. Those records describe the builds
that were actually tested; they are not evidence for an Asura release.
Upstream projects such as Ghostty retain their own names and licenses.

Both repository gates run `scripts/check-product-name.py` to reject retired
product spellings in tracked paths and text outside the historical evidence.
