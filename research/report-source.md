# ScreenFuse research source ledger (internal)

Canonical report: `docs/MARKET_RESEARCH.md`
Audience: product/engineering
Access date: 22 August 2026
Decision: build on Hydra because no exact vendor-independent Windows/macOS/Linux product was found.

## Claim-to-source ledger

- Dell DDPM feature/OS/vendor constraints — Dell Technologies, current DDPM Windows and macOS support pages; URLs embedded in canonical report.
- EasyKVM DDC and platform constraints — EasyKVM/Avendavi, input-switch and troubleshooting pages, crawled 22 August 2026.
- Hydra features/license/platform gaps — PacAnimal/Hydra README, releases, and GPL-2.0 license, accessed 22 August 2026.
- Deskflow external display scripts/file-transfer removal/Wayland limits — Deskflow official GitHub wiki and maintainer discussions, accessed 22 August 2026.
- Synergy manual monitor switching — Symless official FAQ and current feature/system-requirement pages, accessed 22 August 2026.
- ShareMouse features/platforms — Bartels Media official product page, accessed 22 August 2026.
- Mouse Without Borders features/limits — Microsoft Learn, updated 13 June 2026, accessed 22 August 2026.
- LG Dual Controller platforms — LG product support and official guide, accessed 22 August 2026.
- Windows DDC warning/API — Microsoft Learn SetVCPFeature, updated 22 February 2024.
- Linux DDC — ddcutil official documentation, current 2.2.7 notice.
- macOS DDC — waydabber/m1ddc official README.

Contradictions resolved: marketing uses “KVM” for both network input sharing and physical monitor switching. The report records those as separate capabilities and does not infer video/input routing from the KVM label alone.
