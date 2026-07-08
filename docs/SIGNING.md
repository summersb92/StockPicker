# Code signing (Windows Authenticode)

The release binaries are currently **unsigned**. On a first run, Windows SmartScreen
shows *"Windows protected your PC"* → **More info → Run anyway**. That's normal for
indie/unsigned software, but signing removes the warning (eventually or immediately,
depending on the certificate type) and proves the binaries came from you and weren't
tampered with.

This doc explains the realistic options in 2026 and how to turn signing on in the
release workflow (`.github/workflows/release.yml`), which already has a signing step
scaffolded and disabled.

## What changed (why you can't just buy a cheap .pfx anymore)

Since **June 2023**, the CA/Browser Forum requires the private key of every new
code-signing certificate (both OV and EV) to live on **FIPS 140-2 Level 2 hardware** —
a USB token or a cloud HSM. You can no longer get a plain downloadable `.pfx` file for
a publicly-trusted cert. That makes the old "store a .pfx in a GitHub secret and call
signtool" pattern obsolete for public trust, and pushes small projects toward a
cloud-signing service.

## Options, ranked for this project

### 1. Azure Trusted Signing — recommended
Microsoft's cloud signing service (formerly *Azure Code Signing*). Best fit for an
open-source / small project distributing via GitHub Releases.

- **Cost:** ~**$9.99/month** (Basic tier), pay-as-you-go via an Azure subscription.
- **No hardware token** — keys live in Microsoft's HSM; you authenticate from CI.
- **Eligibility:** a verified **organization** (3+ years old) *or* an **individual**
  developer (Microsoft added individual validation; expect an identity check).
- **CI-native:** there's an official GitHub Action (`azure/trusted-signing-action`),
  already wired into our workflow behind the `SIGNING_ENABLED` switch.
- **SmartScreen:** certificates chain to the Microsoft-operated public root and build
  reputation quickly.

**Setup:**
1. Create an Azure subscription → a **Trusted Signing account** → a **certificate
   profile** (Public Trust). Complete identity validation.
2. Create an **App Registration** (service principal) and grant it the
   *Trusted Signing Certificate Profile Signer* role on the account.
3. In the GitHub repo, add:
   - **Secrets:** `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`
   - **Variables:** `SIGNING_ENABLED=true`, `TRUSTED_SIGNING_ENDPOINT`
     (e.g. `https://eus.codesigning.azure.net`), `TRUSTED_SIGNING_ACCOUNT`,
     `TRUSTED_SIGNING_PROFILE`
4. Re-run the release workflow. The `Sign executables` step signs both exes before
   they're zipped. Verify the action's version pin against its releases page.

### 2. OV or EV certificate on a hardware token / cloud HSM
Buy from DigiCert, Sectigo, SSL.com, GlobalSign, etc.

- **OV:** ~$200–400/yr. SmartScreen reputation builds up **over time / downloads**.
- **EV:** ~$300–700/yr. **Immediate** SmartScreen reputation, but always requires a
  hardware token (or the CA's cloud-HSM signing product).
- CI signing means either a cloud-HSM offering from the CA (e.g. SSL.com eSigner,
  DigiCert KeyLocker) with a GitHub Action, or a self-hosted runner physically
  attached to the USB token. A GitHub-hosted runner cannot see a USB token.

Use this if you already have a CA relationship or need EV's instant reputation.

### 3. Self-signed certificate — internal use only
Free (`New-SelfSignedCertificate` + `signtool sign /fd SHA256`), but **not publicly
trusted** — every machine must manually import your cert into *Trusted Root* /
*Trusted Publishers*. Fine for signing builds you deploy to your own/known machines;
does **nothing** for SmartScreen on the public internet. Don't ship this to strangers.

### Not applicable
- **Sigstore / cosign** signs artifacts and containers, not Windows Authenticode — it
  won't clear SmartScreen for an `.exe`.
- **`dotnet` has no built-in Authenticode signing;** you always invoke `signtool` (from
  the Windows SDK) or `AzureSignTool` / the Trusted Signing action.

## Recommendation for StockPicker

- **Short term:** ship unsigned; the README/release notes already tell users about the
  SmartScreen "Run anyway" prompt. This is common and acceptable for a research tool.
- **When ready to invest ~$10/month:** enable **Azure Trusted Signing** — flip the
  `SIGNING_ENABLED` repo variable to `true` and add the Azure secrets/variables above.
  No workflow code changes needed; the step is already in place.

## Verifying a signature
```powershell
Get-AuthenticodeSignature .\StockPicker.exe | Format-List Status, SignerCertificate
# or
signtool verify /pa /v .\StockPicker.exe
```
