# QuotaDuck repository identity assets

Presentation artwork for the GitHub repository: a README header and a GitHub social-preview card. They are **not** part of the application, are not built by anything, and are **not included in the release ZIP** (see `release/MANIFEST.txt`).

| File | Use | Size |
|---|---|---|
| `quotaduck-header.png` | README header (shown in `README.md`) | 1280 × 272, 18,906 bytes |
| `quotaduck-header.svg` | editable source of the header | 8,940 bytes |
| `quotaduck-social.png` | GitHub social-preview card (**not uploaded yet**; the repository owner sets it in the repository settings) | 1280 × 640, 30,992 bytes |
| `quotaduck-social.svg` | editable source of the social card | 9,433 bytes |

The social card follows GitHub's recommended 1280 × 640 size (minimum 640 × 320, under 1 MB). Actual cropping and compression by GitHub and link unfurlers have not been verified.

## Provenance

- **Origin.** A visual-identity package prepared with Codex on 10 October 2026 for visual review (package name `repo-identities-v1`, kept outside this repository). The four files here were copied unchanged from it. The package also contained assets for another project; **none of those are included here**, and neither are its build or check scripts, review sheets or render report.
- **Approval.** The repository owner approved these two designs for the README and the social preview in November 2026. Nothing in this folder claims a release, version or live quota values.
- **Composition.** The layout follows the Patitos Fortune brand-system "specimen" repository composition (Paper field, Ink type, Sage accent, double frame, serif title, sans descriptor, small lab attribution).
- **Pond art.** The two ponds are QuotaDuck's own generated art, embedded byte-for-byte from the scene previews of this repository's generator (`src/QuotaDuck.Art`, `tools/QuotaDuck.AssetGen --previews`): `pond-060.png` (SHA-256 `c701fe17aef9676f05e8…`) and `pond-100.png` (`6612b389ed6afb4ad5ed…`), both 576 × 128, shown at 288 × 64. They are existing art states, not current provider readings. The generator output on the maintainer's machine has identical hashes. Under `NOTICE` this art is original and MIT-licensed.
- **Parent mark.** The small duck beside "PATITOS FORTUNE" is the unchanged `brand/source/geometric-duck.svg` of the Patitos Fortune Brand System v1 (brand-system commit `1c2070a`, 1 October 2026), SHA-256 `5321a3c82a1a367e77c9c5714556df73328bb694c22243af43088e47b8ff7d5f`, embedded in both SVGs.
- **Fonts.** The SVG text uses system font fallbacks (`Georgia`, `Segoe UI`, `Consolas`); no font files are included. The PNGs fix the reviewed appearance, so opening the SVG on a machine with other fonts can look different.
- **Safety review of the SVGs.** Each SVG is self-contained: no scripts, no external or remote references, no absolute paths, and the PNGs carry only the standard image chunks (no text or location metadata).

## File hashes (SHA-256, of the committed bytes)

```
e3c7dd421e094ff461a52c8907d2f094da5ce9344a34441826f994417273dc37  quotaduck-header.png
1ef580c94a266a244e8902d3adda6659275caadb6d5ff638f7611a5164beb4ee  quotaduck-header.svg
d232e2545bce18da9eadb6cdce8297fd147cc36d627697776acb129072fecc38  quotaduck-social.png
2d07a44e172cad51121ff1c2dd030bb94dbd1915b286da08c74ad0440ace1f04  quotaduck-social.svg
```

The two SVG hashes also match the `sourceSHA256` values in the original package's render report.

## Rights and brand use

These images combine two kinds of material with different terms. The composed images as a whole are **not** unconditionally MIT-licensed.

- **QuotaDuck art (MIT).** The existing QuotaDuck-generated pixel ponds, ducks, gauges and tray artwork, including the pond art embedded in these images, remain MIT-licensed under this repository's `LICENSE` and `NOTICE`.
- **Patitos Fortune brand assets (reserved).** The Patitos Fortune name, wordmark and geometric Impossible Duck identity used in these images are subject to the organization's published brand notice, unless separately licensed or authorized: <https://github.com/PatitosFortune/.github/blob/main/BRAND_NOTICE.md>.
- **Composition.** Each image contains both the MIT-licensed QuotaDuck art and the reserved Patitos Fortune brand assets. Using the MIT-licensed art on its own does not carry any right to the brand assets.
- **Affiliation.** QuotaDuck is an official Patitos Fortune project. Third parties must not use the Patitos Fortune identity to imply endorsement or affiliation without permission.

## Changing these assets

Edit the SVG, re-export the PNG at the same size, update the hashes above and keep the text of the project name and tagline in the README as real Markdown. Do not add release numbers or live values to these images.
