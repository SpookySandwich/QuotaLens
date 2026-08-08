# Provider Plan Evidence

Last reviewed: 2026-08-02

This file records the public pricing evidence used by QuotaLens's default plan-value rules. It is
not a claim that a provider's private usage endpoint is officially documented. Retrieval-contract
provenance is tracked separately in `winui/Core/ProviderContracts.cs`, while the pinned CodexBar
registry is tracked in `provider-upstream-lock.json`.

## Evidence policy

- A rule is marked `Official` only when its plan, price, cadence, seat basis, storefront/region,
  source URL, and verification date are stored in `Catalog.DefaultPlanValueRules`.
- `$` prices are recorded as USD for the provider's default US storefront; they are not assumed to
  be globally localized prices.
- `from`, introductory, paused, waitlist, minimum-seat, and first-month conditions are retained in
  the rule metadata instead of being silently discarded.
- Older/private plan values may remain as `LegacyUnverified` sorting estimates. They are not
  official pricing claims and should be removed or re-sourced before being upgraded to `Official`.
  QuotaLens may use them to order recommendations, but never renders them as `$X/mo` in the hero.
- User-edited plan-value rules are `UserConfigured` estimates.

## Current official rules

| Provider | Current monthly rules encoded by QuotaLens | Important qualification | Official source |
| --- | --- | --- | --- |
| Codex / ChatGPT | Free $0; Go $8; Plus $20; Pro 5x $100; Pro 20x $200; Business $25/user | Generic `Pro` is conservatively valued at the $100 minimum when the tier is not returned. Business is $20/user/month when billed annually. | [Pricing](https://chatgpt.com/pricing), [Pro tiers](https://help.openai.com/en/articles/9793128-what-is-chatgpt-pro) |
| GitHub Copilot | Free $0; Pro $10/user; Pro+ $39/user; Max $100/user; Business $19/user; Enterprise $39/user | Some new self-serve Business sign-ups are temporarily paused. | [Plans](https://github.com/features/copilot/plans), [plan documentation](https://docs.github.com/en/copilot/get-started/plans) |
| Claude | Free $0; Pro $20; Max 5x $100; Max 20x $200; Team Standard $25/seat; Team Premium $125/seat | Annual equivalents differ. Generic `Max` uses the $100 minimum when the exact tier is absent. | [Pricing](https://claude.com/pricing), [Max plan](https://support.claude.com/en/articles/11049741-what-is-the-max-plan) |
| Amp | Megawatt $20; Gigawatt $200 | Each tier includes its documented orb hours and at least the matching dollar amount of agent usage. | [Pricing](https://ampcode.com/pricing) |
| Cursor | Hobby $0; Pro $20; Pro+ $60; Ultra $200; Teams Standard $40/user; Teams Premium $120/user | Annual equivalents differ. | [Pricing](https://cursor.com/pricing) |
| Augment | Business $100 flat | Covers up to 50 seats; obsolete Community/Pro/Max defaults were removed. | [Pricing](https://www.augmentcode.com/pricing) |
| Factory | Pro $20; Plus $100; Max $200 | Business and Enterprise are custom. Obsolete Starter and old Pro values were removed. | [Pricing](https://www.factory.ai/pricing) |
| MiniMax Token Plan | Plus $20; Max $50; Ultra $120 | Token Plan credits are separate from PAYG Credits and team seat assignment. | [Token Plan](https://platform.minimax.io/docs/guides/pricing-token-plan) |
| MiMo Token Plan | Lite $6; Standard $16; Pro $50; Max $100 | The international USD storefront and China CNY storefront have separate prices; annual prices differ. | [Token Plan](https://mimo.mi.com/docs/en-US/price/token-plan) |
| ElevenLabs | Free $0; Starter $6; Creator $22; Pro $99; Scale $299; Business $990 | Scale includes three seats and Business includes ten; annual equivalents and promotions differ. | [Pricing](https://elevenlabs.io/pricing) |
| Warp | Free $0; Build from $20; Max from $200; Business from $50/user | The values are starting prices; old Pro/Turbo defaults were removed. | [Pricing](https://www.warp.dev/pricing) |
| Kilo | Platform Individual $0; Teams $15/user; Kilo Pass Starter $19; Pro $49; Expert $199 | Kilo Pass is an inference-credit product, distinct from the platform plan. | [Pricing](https://kilo.ai/pricing), [Kilo Pass](https://kilo.ai/pricing/kilo-pass) |
| Ollama | Free $0; Pro $20; Max $100; Team introductory $25/seat | Max is temporarily paused for new subscriptions. Team is waitlisted with a five-seat minimum. | [Pricing](https://ollama.com/pricing) |
| OpenCode Go | $10 recurring | The first month is $5; one workspace member may subscribe. The unsubstantiated OpenCode Pro $20 rule was removed. | [Go](https://opencode.ai/go), [usage limits](https://opencode.ai/docs/go/#usage-limits) |
| Abacus AI ChatLLM | Basic $10; Pro $20 | Basic is $7 for the first month. Obsolete Free/Enterprise defaults were removed. | [Pricing](https://abacus.ai/pricing) |

## Explicitly not promoted to official pricing

- Windsurf's former pricing URL currently redirects to Devin pricing, so QuotaLens does not treat
  the audit's Windsurf amounts as verified current prices.
- Synthetic's public page did not expose a verifiable current plan price during this review.
- Private-dashboard plan names for BayesDL, Qoder, Manus, T3 Chat, Command Code, Abacus AI, and
  similar connectors are not made official without a public source or a redacted response fixture.
- Enterprise/custom prices are not converted into arbitrary numeric defaults.

## Maintenance

When changing a public plan rule:

1. Open the provider's official source and verify the visible plan name, amount, cadence, seat
   basis, and availability qualification.
2. Update the structured rule and its `LastVerifiedAt` date.
3. Update this evidence table.
4. Run `CatalogConsistencyTests`; every `Official` rule must have complete provenance.
