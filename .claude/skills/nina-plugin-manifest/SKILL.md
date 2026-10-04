---
name: nina-plugin-manifest
description: Prepare and validate N.I.N.A. plugin release packages and manifests for isbeorn/nina.plugin.manifests, including Manual Focuser metadata and compatibility. Use for release or manifest work, rather than ordinary plugin implementation.
---

# N.I.N.A. plugin manifests

Prepare reviewable release artifacts and a manifest. Read [references/release.md](references/release.md) for this repository's identity, build command, upstream tooling, and submission checks. Refresh the linked upstream files when performing a release; requirements can change.

Derive metadata from the final compiled assembly. Preserve the plugin GUID and title. Keep assembly/file versions consistent and select a new version for changed release bytes; check existing public releases before choosing it. Match minimum N.I.N.A. compatibility to the APIs and packages actually used.

Build once, stage only necessary distributable files, and generate the manifest against the exact DLL or archive to be downloaded. Do not rebuild or modify those bytes afterward. Distinguish schema validation from remote URL/checksum verification and from actual N.I.N.A. loading, UI, and equipment tests. Report each check performed and any pending checks.

Preparation alone does not authorize pushing tags, publishing releases, uploading assets, or opening a manifest PR. Follow the user's existing authorization when those actions are explicitly requested. Draft all local artifacts and PR text before seeking any additional authorization. Do not install upstream sample automation with release/PR side effects as part of merely preparing a manifest.

Give the user the package and manifest paths, version/channel, compatibility requirement, validation results, and any remaining publication steps. Include material AI assistance in submission notes according to current upstream policy; maintainer review and hardware testing cannot be claimed on the user's behalf.
