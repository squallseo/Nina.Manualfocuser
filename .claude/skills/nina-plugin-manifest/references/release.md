# Release preparation

## Manual Focuser repository

Inspect `Properties/AssemblyInfo.cs` and `ManualFocuser.csproj` each time; these are observations from 2026-10-04, not fixed release values:

- Title `Manual Focuser`; GUID `56e3434f-95de-49fe-bb59-2034ea457afb`.
- Assembly/file version `1.1.0.0`; assembly `Cwseo.NINA.ManualFocuser.dll`.
- Source `https://github.com/squallseo/Nina.Manualfocuser`; declared license MPL-2.0.
- Target `net8.0-windows`; N.I.N.A. dependencies and minimum application metadata `3.2.0.9001`.
- `GenerateAssemblyInfo=false`: passing MSBuild `Version` does not replace the version attributes in `AssemblyInfo.cs`.
- Build without local host deployment: `dotnet build ManualFocuser.csproj -c Release -p:DeployPlugin=false`.
- The custom PostBuild target copies a DLL into the local N.I.N.A. plugin directory unless disabled. The sample's `PostBuildEvent=` alone does not disable this target.

The [published 1.1.0.0 manifest](https://github.com/isbeorn/nina.plugin.manifests/blob/main/manifests/m/Manual%20Focuser/3.0.0.2017/1.1.0.0/manifest.json) targets N.I.N.A. `3.0.0.2017`; do not reuse that minimum for the current code. Its release URL uses a `v` tag prefix, while the upstream automation sample expects four numeric components without a prefix. Match the actual chosen tag and asset filename consistently.

## Official generator and automation

Use the current [CreateManifest.ps1](https://github.com/isbeorn/nina.plugin.manifests/blob/main/tools/CreateManifest.ps1) in PowerShell 7, downloaded to a temporary tooling directory and inspected before execution. Run from a dedicated staging directory because it writes `manifest.json` and optional ZIP there. It reads file-version and metadata attributes without executing the plugin. Supply `-file` and `-installerUrl`; use `-createArchive` and optionally `-includeAll` only with a curated package folder. `-beta` selects the beta channel. The checksum covers the ZIP for archive mode, otherwise the DLL.

PowerShell 7 is required; Windows PowerShell 5.1 is not an equivalent runtime. If unavailable, report the missing prerequisite or install it only within the user's authorized scope. Never use upload switches for local preparation.

The actual [workflow sample](https://github.com/isbeorn/nina.plugin.manifests/blob/main/tools/github-action.yaml) has a `.yaml` extension, despite the README's `.yml` link. It builds on a version tag and publishes a GitHub release; optional follow-on jobs push to a manifest fork and create a PR using PAT. Adapt names, staging files, framework, version handling, and the custom deploy suppression before adopting it. For preparation-only tasks, omit external publishing jobs and do not add credentials.

## Validation and submission

Use the current [schema](https://github.com/isbeorn/nina.plugin.manifests/blob/main/manifest.schema.json). Required metadata includes identity, four-part plugin and application versions, author, repository/license information, short description, and installer details. Installer type is DLL or ARCHIVE; checksum algorithms are MD5, SHA1 or SHA256. Prefer SHA256. Schema acceptance alone does not verify artifact existence, contents, or compatibility.

In a separate manifest-repository checkout, install its Node dependencies and run `node gather.js` for the documented schema check. Review output for the specific manifest. The [CI validator](https://github.com/isbeorn/nina.plugin.manifests/blob/main/validate-latest-manifest.js) also downloads and hashes installers, but selects changed JSON files from `HEAD~1..HEAD`; it does not inspect arbitrary uncommitted staging files. Run it only when its selected commit actually contains the prepared manifest. A not-yet-published installer URL cannot pass remote validation; mark that check pending rather than publishing without authorization.

Independently compare `Get-FileHash -Algorithm SHA256` of the staged artifact with the manifest. After authorized publication, download the installer URL to a separate temporary file and compare the hash again. Never replace a published asset with different bytes while leaving its manifest unchanged.

Follow the [current contribution README](https://github.com/isbeorn/nina.plugin.manifests#submitting-a-plugin-manifest): use a fork and manifest path `manifests/m/Manual Focuser/<minimum NINA version>/<plugin version>/manifest.json`, preserving older compatible releases. Beta manifests use `Channel: Beta`. Upstream requires open source distribution, accurate license/source declarations, an accountable human maintainer, and disclosure of material AI use. Prepare PR notes stating changes, compatibility, test evidence, and that disclosure; do not assert human review or equipment testing that has not occurred.
