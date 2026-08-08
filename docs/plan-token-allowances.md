# Plan token allowances — research provenance (2026-08-06)

Source for Catalog.DefaultPlanTokenRules: per-platform research cross-validated
adversarially (multi-agent, Opus 5). Three deliberate deviations from the raw
synthesis are noted in the catalog comments (Kimi Moderato ladder-consistent 66,
Amp Free capped at 3, bare Codex pro conservative at the 5x minimum).

# QuotaLens — Final Per-Plan Weekly Token Allowance Table

**Canonical metric:** total tokens processed per week, **cache-inclusive** (ccusage convention: `input + output + cache_create + cache_read`). All figures below are normalized to this basis.

**Verdict application:** 5 plans dropped (`reject`), 27 adjusted, 45 confirmed as-is. Dropped: Windsurf Enterprise, Factory Free/BYOK, Warp Pro (legacy), Warp Turbo (legacy), Warp Lightspeed (legacy).

---

## 1. Normalized Table

### Claude Code (`claude`)

| Plan | matchPatterns | $/mo | Weekly M | Conf. | Basis |
|---|---|---:|---:|---|---|
| Free | `free` | 0 | **0** | official | Claude Code requires Pro/Max/Team/Enterprise/Console — Free has no CLI access at all |
| Pro | `pro` | 20 | **100** ⬇ | derived | Re-anchored on Anthropic's only current published figure ($150–250/dev/mo, p90 $30/active-day) at $0.55/MTok; the 40–80 Sonnet-hr figure is stale and the +50% promo is unverifiable |
| Max 5x | `max 5x`, `max5x`, `5x`, `max` | 100 | **350** ⬇ | derived | Published weekly Pro:Max5x ratio is 3.5x (not 5x — that's burst); Opus is a sub-cap inside the shared pool, not additive |
| Max 20x | `max 20x`, `max20x`, `20x` | 200 | **600** ⬇ | derived | Weekly ratio to Pro is ~6x; GitHub #48732 (solo dev exhausts cap in ~1 week) caps the plausible ceiling well below 1350 |
| Team Standard | `team standard`, `standard seat`, `team` | 25 | **100** ⬇ | speculative | Same 5h+weekly seat metering as Pro; mirrors corrected Pro. Price corrected $30→$25 monthly |
| Team Premium | `team premium`, `premium seat` | 125 | **350** ⬇ | speculative | Priced at the Max 5x point, marketed as the heavy-workload seat; mirrors corrected Max 5x |
| Enterprise | `enterprise` | ~20 + API usage | **350** ⬇ | speculative | Seat-based mirrors Team Premium. **Usage-based Enterprise is uncapped — render as "unmetered", not as a bar** |

### Codex / OpenAI (`codex`)

| Plan | matchPatterns | $/mo | Weekly M | Conf. | Basis |
|---|---|---:|---:|---|---|
| Free | `free` | 0 | **1.5** | speculative | No official anchor; reconstructed from ~3–8 short turns/day. Credit-share reasoning in the source is invalid but the magnitude survives |
| Go | `go` | 8 | **11** | speculative | ~35% of Plus pool; cross-checks as slightly worse value/$ than Plus, the correct direction for an entry tier |
| Plus | `plus` | 20 | **32** | derived | Anchor. Official 5h message ranges × official credit rate card collapse to one ~400–500 credit/5h pool across all three models; ×4 weekly multiplier; 17k tokens/credit |
| Pro 5x | `pro 5x`, `pro5x`, `5x` | 100 | **160** | derived | Official 5h table is *exactly* 5x Plus on every model row |
| Pro (20x) | `pro`, `pro 20x`, `pro20x`, `20x` | 200 | **600** | derived | Official 5h table exactly 20x Plus; ~6% haircut for sub-linear weekly scaling. `plan_type` cannot distinguish $100 from $200 — the 5x patterns must be tested first |
| Team (legacy) | `team` | 30 | **32** | derived | Per-seat limits identical to Plus; the extra $10 buys admin, not tokens |
| Business | `business` | 25 | **32** | derived | Official limits row identical to Plus ($20/user annual, $25 monthly) |
| Enterprise / Edu | `enterprise`, `edu` | ~60* | **45** | speculative | Official footnote says per-seat = Plus (i.e. 32M); the 1.4x premium for flexible-pricing contracts is unsourced. *Contract-priced; $60 is a chart-axis placeholder |

### Google AI — emitted for both `gemini` and `antigravity`

| Plan | matchPatterns | $/mo | Weekly M | Conf. | Basis |
|---|---|---:|---:|---|---|
| Free / base | `free`, `base`, `no plan`, `individual` | 0 | **8** | derived | ~20 agent req/day (cut from 250 in Dec 2025), weekly-only refresh, Flash-weighted. May 2026 3x relief was paid-tiers-only |
| Google AI Plus | `plus` | 4.99 ⬇ | **8** ⬇ | speculative | Antigravity docs recognize only Ultra / Pro / "not on Pro or Ultra" — Plus falls into the baseline pool. Price cut $7.99→$4.99 |
| Google AI Pro | `pro` | 19.99 | **30** ⬇ | derived | Source's own unit-economics check had a 4.3x weekly/monthly error (implied 17x subsidy, not 4x). Corrected via three routes: unit economics 14–35M, AI-credit anchor 7–18M, post-May request path 35–50M |
| Google AI Ultra (20x) | `ultra max`, `max` | 199.99 | **600** ⬇ | derived | Official 5x/20x ratios are real but Gemini-app-scoped ("up to"); Antigravity docs publish no multiplier. 20 × corrected Pro base |
| Google AI Ultra (5x) | `ultra` | 99.99 | **150** ⬇ | derived | 5 × corrected Pro base. Read as the generous end of a 50–150M band; a weekly cap still binds on this tier (unlike 20x) |

**Do not double-count.** Gemini CLI retired 18 Jun 2026; Gemini and Antigravity draw from one Google AI wallet. Show one bar per Google subscription.

### Kimi For Coding (`kimi`)

| Plan | matchPatterns | $/mo | Weekly M | Conf. | Basis |
|---|---|---:|---:|---|---|
| Adagio (Free) | `adagio`, `free` | 0 | **0** | official | Kimi Code cell is blank on the official tier table; product page lists only Andante+ |
| Andante | `andante`, `basic` | 7 (CN-only) | **17** ⬇ | derived | Official Kimi Code credit ladder recovered (CN 1x/4x/20x/60x = intl 1x/5x/15x/30x). Anchored on measured Allegretto: 330 × 1/20. The 1024 req/wk cap is *not* binding — the shared credit pool is |
| Moderato | `moderato`, `intermediate` | 19 | **92** | derived | 2048 req/wk × 45k. ⚠ Ladder-consistent value is 66M (330 × 4/20); 92 is 1.39x high |
| Allegretto | `allegretto`, `advanced` | 39 | **330** | community-measured | Golden0Voyager telemetry: ~320M/wk measured on a live account; hvoy.ai independently gives 357M/7d. 91.9% of it is cache reads |
| Allegro | `allegro` | 99 | **900** | derived | Official ladder puts Allegro at exactly 3x Allegretto (60x/20x CN = 15x/5x intl) → 990M ladder-implied |
| Vivace | `vivace` | 199 | **1750** | derived | Overseas-only tier (CN help page: "含海外套餐 Vivace"); intl table publishes "Kimi Code credits 30x" = exactly 2x Allegro → 1980M ladder-implied |

### Cursor (`cursor`)

| Plan | matchPatterns | $/mo | Weekly M | Conf. | Basis |
|---|---|---:|---:|---|---|
| Hobby (Free) | `hobby`, `free` | 0 | **2** | speculative | No published number anywhere; deliberate floor estimate |
| Pro | `pro` | 20 | **120** ⬇ | derived | Two-measurement back-solve to a ~$190 first-party pool holds, but the claimed "permanent 21 Jul 2026 2x doubling" is absent from Cursor's changelog. $20 third-party pool official (~3.5M/wk of it) |
| Pro+ | `pro+`, `pro plus`, `proplus` | 60 | **150** ⬇ | derived | "3x Pro limits on Agent" maps onto the $20→$70 dollar pool, not the in-house pool (docs show identical "Generous included usage" for every tier) |
| Ultra | `ultra` | 200 | **250** ⬇ | derived | "20x Pro" is exactly $400/$20 — the multiplier is fully explained by the third-party pool with no first-party scaling. ~$80M/wk third-party + modest first-party headroom |
| Teams Standard | `teams`, `team`, `business` | 40 | **120** ⬇ | derived | Pricing page lists *no* agent multiplier for Standard (only Premium gets one) — parity with Pro. Extra $20 buys SSO/analytics |
| Teams Premium | `teams premium`, `team premium`, `premium` | 120 | **160** ⬇ | speculative | "5x Standard limits on Agent" applies to the dollar pool; ~$100 third-party + Pro-ish first-party |

### Windsurf / Cognition (`windsurf`)

| Plan | matchPatterns | $/mo | Weekly M | Conf. | Basis |
|---|---|---:|---:|---|---|
| Free | `free` | 0 | **0.5** | speculative | "Light quota"; ~2.5 sessions at the docs' own 200k-token example. Unlimited SWE-1.7 doesn't count against quota, so real throughput is unbounded |
| Pro | `pro` | 20 | **7** | derived | Re-derived token-natively (Cascade retired Jul 2026; quota now metered in tokens). Adaptive at $0.50/$2.00/$0.10 with agentic cache mix ≈ $0.28–0.53/M against ~$20–30/mo of API value |
| Teams | `teams`, `team` | 40 + $80 base | **7** | derived | Docs group them: "Pro and Teams full seats: Include a daily and weekly allowance." Extra spend is governance. ⚠ True structure is $80/mo platform + $40/seat |
| Max | `max` | 200 | **44** | derived | ~6.25x Pro. Note the six-ratio table backing that multiplier is no longer published. Max has a weekly allowance only, no daily cap |
| ~~Enterprise~~ | — | — | **DROPPED** | — | **Rejected:** custom-priced and billed in ACUs (compute units), not tokens — not on the self-serve quota system at all. Render "Custom / negotiated (ACU-based)", no bar |

### GitHub Copilot (`copilot`)

| Plan | matchPatterns | $/mo | Weekly M | Conf. | Basis |
|---|---|---:|---:|---|---|
| Free | `free` | 0 | **0.4** | speculative | 50 chat requests/mo is the binding cap (credit allowance undisclosed); 2,000 completions excluded |
| Pro | `pro` | 10 | **3.5** | derived | 1,000 base + 500 flex = 1,500 credits = $15 of model spend at 1 credit = $0.01; ÷ $1.065/MTok blended (85/12/3 cache/input/output on Sonnet-class rates) |
| Pro+ | `pro+`, `pro plus` | 39 | **16** | derived | 3,900 + 3,100 = 7,000 credits = $70. Opus 4.8 access pulls realized value toward the low end |
| Max | `max` | 100 | **46** | derived | 10,000 + 10,000 = 20,000 credits = $200. Credit ratio to Pro+ is exactly 2.86x, matching GitHub's "2.9x+" claim |
| Business | `business` | 19 | **4.4** | derived | 1,900 credits = $19, pooled org-wide. ⚠ Promo of 3,000 credits runs 1 Jun – 1 Sep 2026 (6.9M/wk) — do not encode |
| Enterprise | `enterprise` | 39 | **9** | derived | 3,900 credits = $39, exactly 2x Business. ⚠ Same promo (7,000 credits, 16.1M/wk) through 1 Sep 2026 |

### Qoder (`qoder`)

| Plan | matchPatterns | $/mo | Weekly M | Conf. | Basis |
|---|---|---:|---:|---|---|
| Community (Free) | `community`, `basic`, `free` | 0 | **0.5** | speculative | Undisclosed daily basic-model cap; not credit-derived, so unaffected by the constant correction |
| Pro Trial | `pro trial`, `trial` | 0 | **1.5** ⬆ | derived | 300 credits / 14 days = 150 cr/wk × corrected constant |
| Pro | `pro` | 20 | **4.6** ⬆ | derived | 2,000 cr/mo. Credit→token constant corrected 4,000 → **~10,000 tok/cr**: every published per-task anchor (Agent 7cr@50K ≈ 10.7k, 12cr@200K ≈ 19.8k, Ask@50K ≈ 17k, Repo Wiki 50cr/repo) exceeds 4k |
| Pro+ | `pro plus`, `pro+` | 60 | **14** ⬆ | derived | 6,000 cr = exactly 3x Pro; per-task credit costs are plan-independent |
| Ultra | `ultra` | 200 | **46** ⬆ | derived | 20,000 cr = 10x Pro. Ultimate tier confirmed at price_factor 1.6; the free-Ultimate-calls promo expired 31 Jul 2026 |
| Teams | `team` | 40 | **6.9** ⬆ | derived | 3,000 cr/seat = 1.5x Pro. ⚠ Teams credits are **not** pooled ("cannot be transferred or shared"); the shared org pool is the separate $20/seat Enterprise product |

### MiMo / Xiaomi (`mimo`)

| Plan | matchPatterns | $/mo | Weekly M | Conf. | Basis |
|---|---|---:|---:|---|---|
| Max | `max` | 100 | **390** | derived | 82B credits/mo ÷ 48 blended credits/token. 1 credit = ¥1e-8 verified against the pay-as-you-go card; no weekly or 5h sub-cap ("无周限额 · 无 5 小时限额") |
| Pro | `pro` | 50 | **180** | derived | 38B credits. Xiaomi's own round-count guidance implies 6.4–6.9M credits/round uniformly across Lite/Standard/Pro/Max, confirming linear-in-pool scaling |
| Standard | `standard` | 16 | **53** | derived | 11B credits. ¥99 buys ¥110 of list API value — a 1.11x discount, so the big token counts come from the ¥0.025/MTok cache-hit rate, not subsidy |
| Lite | `lite` | 6 | **20** | derived | 4.1B credits, 0.373x Standard |
| Free / trial | `free`, `trial`, `payg` | 0 | **1** ⬇ | speculative | The ¥10 grant is **referral-gated and capped at the first 30 people**, not a general signup credit. Honest recurring value is 0; kept at 1 only to rank below Lite |

### Kiro / AWS (`kiro`)

| Plan | matchPatterns | $/mo | Weekly M | Conf. | Basis |
|---|---|---:|---:|---|---|
| Free | `free` | 0 | **0.5** | derived | 50 credits/mo ÷ 4.345 × 40k tok/credit |
| Pro | `pro` | 20 | **9** | derived | 1,000 credits/mo. Conversion band is 20–45k tok per 1.0x credit; 40k sits at the optimistic end (a 7M print would be better centered) |
| Pro+ | `pro+`, `pro plus` | 40 | **18** | derived | 2,000 credits = exactly 2x Pro at the identical $0.02/credit |
| Pro Max | `pro max`, `promax` | 100 | **46** | derived | 5,000 credits. **Currently missing from `Catalog.cs`** — a Pro Max account silently matches the `pro` rule today (5x undervaluation). Insert before `pro` |
| Power | `power` | 200 | **92** | derived | 10,000 credits = exactly 10x Pro |

### BayesDL (`bayesdl`)

| Plan | matchPatterns | $/mo | Weekly M | Conf. | Basis |
|---|---|---:|---:|---|---|
| Coding Pro 进阶包 | `coding pro` | 5.60 | **110** | derived | 18,000 **次** (calls)/mo — unit confirmed in BayesDL's own React bundle (`次` when `isCodingPlan===1`). 4,143 calls/wk × 30k tok/call. No 5h/weekly sub-window exists (grepped all 282 JS chunks). Cleaner central is 124M |
| Token Pro 进阶包 | `token pro` | 5.60 | **4** | derived | 20M tokens/mo ÷ 4.345 = 4.6M nominal; the 1.15x "magnification haircut" is unverifiable (no such strings in the bundle) so 4.6 is better-evidenced |
| Token Standard 标准包 | `token standard` | 2.80 | **2** | derived | 10M tokens/mo (nominal 2.30M/wk) |
| Token Lite 体验包 | `token lite`, `体验包` | 0.70 | **0.5** | derived | 2.5M tokens/mo (nominal 0.575M/wk) — ~83 agentic calls total |
| 千万Token免费领 | `免费`, `千万token` | 0 | **2.3** ⬆ | speculative | Grant validity is **1 month**, not 12 (rules: "自发放到账之日起有效期为1个月"), so 10M/4.345. ⚠ **Recommend reclassifying as a promo, not a plan** — one-time, China-Mobile-Jiangsu-only, and reward varies by campaign variant |

### Amp / Sourcegraph (`amp`)

| Plan | matchPatterns | $/mo | Weekly M | Conf. | Basis |
|---|---|---:|---:|---|---|
| Free | `free` | 0 | **5** ⬇ | speculative | **No longer ad-supported** (ads discontinued 30 Mar 2026) and no longer a marketed tier. Nominal $10/day runs Opus-class smart mode (~$2.3/M), so nominal ≈ 30M/wk; ~17% realization given the growth pause and ongoing allowance cuts |
| Megawatt | `megawatt` | 20 | **4** | derived | "$20 included agent usage" at literal zero markup. Low/medium modes only (GLM-5.2 / GPT-5.6 Sol) → ~$1.2–1.4/M blended |
| Gigawatt | `gigawatt` | 200 | **24** | derived | "$200 included agent usage", all modes incl. ultra (Fable 5 agent, ~$4.6/M blended). ⚠ A realistic high/ultra mix gives ~16M/wk — 24 is the optimistic end |
| Pay-as-you-go | `pay-as-you-go`, `pay as you go`, `payg` | 0* | **6** | speculative | No allowance exists at all. Assumes a ~$40/mo top-up. *Not a price — mark as indicative, not a quota |
| Enterprise | `enterprise` | 0* | **30** | speculative | Contract-priced; enterprise usage costs 50% more (≈33% fewer tokens/$). BYO inference keys make the platform quota meaningless when active |

### Factory / droid (`factory`)

| Plan | matchPatterns | $/mo | Weekly M | Conf. | Basis |
|---|---|---:|---:|---|---|
| ~~Free / BYOK~~ | — | — | **DROPPED** | — | **Rejected:** does not exist. Lowest Individual tier is Pro $20. BYOK and Droid Core are *paid-plan features* ("BYOK is free up to an allowance on all Individual plans"; Droid Core is the overflow pool after Standard Usage) |
| Pro | `pro` | 20 | **9** | community-measured | 20M billed standard tokens/mo (third-party-sourced; Factory publishes no token counts). Billed→raw ≈ 0.42–0.50 given ~90% cache discount. Independent check: $20 × ~2.7x subsidy ÷ $1.6/M = 7.8M/wk |
| Plus | `plus` | 100 | **45** | derived | Officially "~5x Pro usage" — shaded slightly below pure 5x because the 5h rolling window makes a bigger monthly pool hard to drain |
| Max | `max` | 200 | **90** | derived | Officially "~10x Pro usage". Dollar cross-check: $200 × 2.7x ÷ $1.6/M = 78M/wk |
| Business | `business`, `team` | 0* | **45** | speculative | Custom, ≤150 seats. Org usage is contract-governed and explicitly **outside** the Individual rate-limit model |
| Enterprise | `enterprise` | 0* | **120** | speculative | Custom, unlimited seats, "dedicated compute with partitioned inference" — provisioned capacity, not a token quota |

### Warp (`warp`)

| Plan | matchPatterns | $/mo | Weekly M | Conf. | Basis |
|---|---|---:|---:|---|---|
| Free | `free` | 0 | **0** ⬇ | official | Refuted on two live sources: "The Free plan doesn't include bundled AI usage for the Warp Agent." BYOK **is** available on Free (the original note had this backwards) |
| Build | `build` | 20 | **3** | official | Best-evidenced conversion in the dataset: "1,500 credits ($20 of included agent usage at API rates)", restated in Warp's 4 Aug 2026 blog. ÷ $1.4–2.3/M agentic blend |
| Max | `max` | 200 | **35** | official | "18,000 credits (12× the included usage of Build)" = $240 of API value for $200. 35/3 ≈ 12x — internally exact |
| Business | `business` | 50 | **3** | official | 1,500 credits/seat — identical to Build. The extra $30/seat buys administration, not inference |
| Enterprise | `enterprise` | 0* | **6** | speculative | Custom shared credit pools; assumed ~2x Build per seat. BYO-LLM makes the quota potentially irrelevant |
| ~~Pro / Turbo / Lightspeed (legacy)~~ | — | — | **DROPPED** | — | **Rejected:** all three unverifiable on any live Warp surface, migrated to Build/Max after 1 Dec 2025, and the requests→tokens conversion is circular (derives 12k tok/request from the same credit anchor it then uses as a check) |

---

## 2. Sanity Check

### 2.1 Within-platform absolute monotonicity (higher price ⇒ more tokens)

**Holds in every platform.** Codex, Google, Kimi, Copilot, Qoder, MiMo, Kiro, Factory, Warp are strictly monotone. Claude, Cursor, and Windsurf are monotone with documented ties (see 2.3).

### 2.2 The headline cross-platform checks the brief asked for

| Check | Result | Verdict |
|---|---|---|
| Claude Max 20x ≫ Claude Pro | 600 vs 100 = **6.0x** | ✅ Correct direction and magnitude. Note this is the *weekly* ratio — the "20x" badge describes the 5-hour burst window, not the weekly ceiling |
| Claude Pro vs ChatGPT Plus, both $20 | 100 vs 32 = **3.1x** | ✅ Directionally right — Claude Code is the more heavily subsidized product. Codex meters in dollar-denominated credits; Claude does not |
| $200 tier convergence | Claude Max 20x **600**, Codex Pro **600**, Google Ultra 20x **600** | ✅ Three independently derived estimates landing on the same number is a genuine cross-validation, not a coincidence of method — they used entirely different anchors (Sonnet-hours, credit rate card, ×20 multiplier) |
| Tokens-per-dollar range | 0.06 M/$ (Warp Business) → 19.6 M/$ (BayesDL Coding Pro) = **~330x** | ⚠ Very wide, but structurally explained: pass-through-at-API-cost (Warp, Amp, Copilot, Qoder) vs subsidized first-party inference (Claude, Cursor, Kimi) vs Chinese carrier-subsidized coding plans (BayesDL, MiMo) |

### 2.3 Flagged: price-per-token anomalies **within** a platform

These are ranked by how likely they are to look like a bug in the UI.

**🔴 Severe — recommend action before shipping**

1. **Amp Free (5M, $0) > Amp Megawatt (4M, $20).** A free tier outranking the entry paid tier is the only true value inversion in the catalog. It is *explicable* (Amp Free was a growth loss-leader, is growth-paused, is actively being cut, and no longer appears on Amp's pricing page at all) but it will read as a bug. **Recommend capping Amp Free at ≤3M so it sorts below Megawatt, or dropping it entirely alongside the rejected Factory Free.**

2. **Amp Pay-as-you-go (6M) and Amp/Factory/Warp Enterprise carry `$0` prices with non-zero token bars.** These are category errors on a price axis: PAYG's 6M assumes a $40/mo top-up, and the Enterprise rows assume $200–300/seat contracts. **Exclude all `$0`-priced non-free rows from any tokens-per-dollar computation or sort.**

3. **BayesDL free-claim promo (2.3M, $0) > Token Lite (0.5M, $0.70) and > Token Standard (2.0M, $2.80).** A one-time, province-locked promotional grant outranks two paid tiers. **Recommend reclassifying as a promo badge rather than a plan row.**

4. **Cursor's value curve collapses 4.8x across its own ladder:** Pro 6.0 M/$ → Pro+ 2.5 → Ultra 1.25. Ultra at $200 delivers only **2.08x** Pro's tokens for **10x** the price. This is the direct, honest consequence of the adversarial finding that Cursor's marketed 3x/20x multipliers map onto the third-party dollar pool ($20→$70→$400) and not the undisclosed first-party pool. It is internally consistent, but it is the single most assumption-sensitive series in the catalog — **if Cursor genuinely honors 20x on the in-house pool, Ultra is 3–4x low.** Flag Cursor's paid tiers as low-confidence in the UI.

**🟡 Moderate — real, published, but counterintuitive**

5. **Copilot Enterprise ($39 → 9M) is 1.8x worse than Copilot Pro+ ($39 → 16M) at the identical price.** Genuine and officially published: Pro+ carries a flex allotment (3,900 base + 3,100 flex = $70 of credits) while Enterprise does not (3,900 credits = $39, 1:1 with seat price). Not an artifact — worth a tooltip.

6. **Kimi Andante ($7 → 2.43 M/$) is 3.5x worse value than Allegretto ($39 → 8.46 M/$).** Unusual shape (cheapest tier = worst value/$), but it follows directly from the official Kimi Code credit ladder (1x / 4x / 20x / 60x on the CN scale) being far steeper than the published request counts (1024 / 2048 / 7168). The credit pool binds, not the request cap.

7. **Kimi Moderato is off-ladder.** Our numbers give Moderato : Allegretto : Allegro : Vivace = 1 : 3.59 : 9.78 : 19.0, but Moonshot's *published* international ladder is 1 : 5 : 15 : 30. Anchoring on the community-measured Allegretto, ladder-consistent values would be **Moderato 66 / Allegretto 330 / Allegro 990 / Vivace 1980**. Moderato at 92 is 1.39x high. **If you want the six Kimi rows internally consistent with the one official scaling law Moonshot publishes, use 66.**

8. **Kimi Vivace (1750M, $199) is ~2.9x the Claude/Codex/Google $200 consensus (600M).** Kimi's numbers rest on a directly measured account, and Kimi's cache-read share (91.9%) is the highest of any platform — but this bar will dominate the chart. Note the tier's own economics get strained: 1,750M gross tokens/wk at K3 rates is ~$3,700/mo of list value on a $199 plan.

9. **Same-price, same-vendor 27.5x gap at BayesDL:** Coding Pro (110M) vs Token Pro (4M), both ¥40. Verified as *internally consistent*, not an error: point the same agentic workload at Token Pro and each 30k-token call burns 30k from the 20M pool (≈667 calls/mo) versus 18,000 calls on the Coding Plan — exactly the ~27x ratio. Same workload, different metering unit.

**🟢 Benign — deliberate design, no action**

10. **Ties where a higher price buys governance, not tokens.** Claude Team Standard ($25) = Pro ($20) = 100M. Windsurf Teams ($40) = Pro ($20) = 7M. Codex Team/Business ($25–30) = Plus ($20) = 32M. Cursor Teams Standard ($40) = Pro ($20) = 120M. Warp Business ($50) = Build ($20) = 3M. All are officially published parity — surface it in the UI as a feature, not hide it.

11. **Inverse value-ladder shapes are platform-characteristic, not errors.** Claude, Cursor, Amp, Windsurf degrade in tokens/$ as tiers rise (entry tier is the loss-leader). Codex, Google, Kimi, MiMo, Copilot *improve* (volume discount). Kiro, Qoder, Factory are perfectly linear ($0.02/credit, $0.01/credit, and flat multiples respectively). The Codex Plus→Pro jump (1.6 → 3.0 M/$) is the most aggressive volume discount and depends entirely on the unverified assumption that the published 20x 5-hour multiplier also applies to the unpublished weekly cap — **this is the single number to update if OpenAI ever publishes weekly caps.**

### 2.4 The one methodological caveat that outranks all the above

Every figure is **cache-inclusive**. In real agentic sessions cache reads are 85–92% of all tokens moved (Claude ~90–95%, Kimi measured 91.9%, Cursor measured 85%). **If any provider is ever added on a non-cached or billable-token basis, its bar will be ~10–15x too short** and the whole comparison silently breaks. Store the metric basis as a field, not as a comment.

---

## 3. Canonical Unit and Scaling for the Cumulative Bar

### 3.1 Canonical unit

```
weeklyTokensMillions : double    // total tokens/week, cache-inclusive (ccusage)
                                 // = input + output + cache_create + cache_read
```

Store **millions**, not raw tokens: the catalog spans 0 → 1,750 in millions, which fits a `double` with no precision theater, keeps JSON readable, and matches how every source reports.

**Do not store a monthly figure and divide at render time.** Six platforms (Qoder, Kiro, MiMo, BayesDL, Cursor, Warp) are natively monthly and were converted at ÷4.345; the rest are natively weekly. Baking the conversion into the catalog keeps one unit in the code path. Weekly is the right canonical window because it is the *binding* cap on the platforms where a binding cap exists (Claude, Codex, Google, Kimi, Windsurf, Factory).

### 3.2 The value to render

```
tokensRemaining = weeklyAllowance × availablePercent
```

Confirmed sound. But three guards:

- **Clamp `availablePercent` to [0,1].** Kimi's endpoint normalizes limits to 100 for some accounts; Qoder and BayesDL return raw pools. Don't trust the provider's arithmetic.
- **Do not render a segment when `weeklyAllowance == 0`.** Claude Free, Kimi Adagio, and Warp Free are structural zeros ("the coding agent is not available on this plan"), not small quantities. Show them in a "not available on this plan" list beneath the chart, never as a zero-width sliver.
- **Suppress the bar entirely for unmetered / BYOK / contract states**, replacing it with an "unmetered" chip: Claude usage-based Enterprise, Factory Business + Enterprise, Amp Enterprise (BYO keys), Warp Enterprise, Windsurf Enterprise (ACU-billed), and any provider detected in BYOK mode. A proportional bar against an uncapped or negotiated pool is not just imprecise, it is meaningless.

### 3.3 Scaling approach

The catalog spans **0.4M → 1,750M — 4.4 orders of magnitude.** On a linear stacked bar, Copilot Free (0.4M) is 1/4400th of Kimi Vivace and physically unrenderable.

**Recommendation: keep the geometry linear, and fix visibility with layout rather than with a transform.**

1. **Linear stacking is non-negotiable for a *cumulative* bar.** A stacked bar's contract with the viewer is that segment widths sum to the total. `log(a+b) ≠ log(a)+log(b)` and `√(a+b) ≠ √a+√b`, so any compressive transform makes the bar sum to something that is not the total. That is a chart that lies. If you want compression, it belongs on a *grouped* (side-by-side) bar, not a stacked one.

2. **Minimum segment width of 4px**, absorbed proportionally from the largest segments. Preserves clickability and hover targets without meaningfully distorting the big segments (4px out of a ~600px bar is <1%).

3. **Sort segments descending, then roll up the tail.** Any provider contributing <1% of the total collapses into a single "Other (n)" segment that expands on click. With a Kimi Vivace or Claude Max account connected, that will be most of the list — which is honest information, not a failure.

4. **Offer a second view, not a second scale.** A per-provider row chart — one bar per provider, each normalized 0–100% of *its own* allowance — answers "how much of each plan have I burned?", which is the question users actually ask most. Keep the cumulative token bar for "how much total capacity do I have left this week?" Two charts, two questions, both linear. This also sidesteps the entire log/sqrt debate.

5. **Encode confidence visually, not numerically.** Render `speculative` segments at ~60% opacity or with a subtle diagonal hatch, and `derived` at full opacity with a dotted top border; `official` and `community-measured` solid. In a Fluent-styled app this reads as texture rather than as clutter, and it stops a placeholder Enterprise bar from looking as authoritative as a measured Allegretto bar. Use the system amber accent for the hatch rather than a new color.

### 3.4 Free tiers

Three distinct cases — do not collapse them:

| Case | Plans | Handling |
|---|---|---|
| **Structurally unavailable** (weight = 0) | Claude Free, Kimi Adagio, Warp Free | No segment. Listed as "Claude Code requires Pro or above" under the chart. A 0-width segment with a tooltip is worse than an explicit sentence |
| **Real but tiny** (weight > 0) | Copilot Free 0.4, Windsurf Free 0.5, Qoder Community 0.5, Kiro Free 0.5, Cursor Hobby 2, Codex Free 1.5, Google Free 8, Amp Free 5 | Normal segment, subject to the 4px floor and the "Other" rollup. Mark `speculative` — six of these eight are unpublished guesses |
| **One-time grants, not allowances** | BayesDL 千万Token, MiMo ¥10 referral credit, Kiro's 500 bonus credits, Factory's signup grant, Amp's first-month 2x | **Never on the recurring weekly axis.** Render as a separate "bonus" chip or a lighter overlay on the segment. Amortizing a one-shot grant into a weekly rate is a category error and produced two of the largest errors the adversarial pass caught |

### 3.5 Unknown plans — default weight

**Do not use a single global constant.** Generosity at the same price point spans **40x** ($20 tier: Warp Build 3M → Cursor Pro 120M), so a global default is wrong by more than an order of magnitude for most providers.

**Recommended fallback chain:**

```
1. Exact pattern match on plan name           → catalog value
2. Provider's cheapest paid tier              → catalog value, flagged "estimated"
3. Global default                             → 15M/week, flagged "estimated"
4. No provider match at all                   → no segment; show "unknown plan" chip
```

Step 2 is the important one and covers nearly every real miss (a new tier launches, or a display name drifts). Deliberately conservative: it under-states rather than over-states, so a new Claude tier doesn't briefly claim 600M of the user's bar.

The **15M** global default is the geometric mean of the fourteen ~$20 tiers in this catalog (≈16.4M, rounded down). Geometric, not arithmetic — the arithmetic mean is ~35M and is dragged upward by Cursor and Claude into a value that is wrong for two-thirds of the platforms.

Any segment reached via step 2 or 3 must render with the hatch/opacity treatment from §3.3.5 and expose the reason in its tooltip ("estimated — plan 'Ultra Max' not in catalog"). Silent fallbacks are how a 5x undervaluation like the current Kiro Pro Max bug goes unnoticed.

### 3.6 Match-order requirement (load-bearing)

The matcher must be **first-match over an ordered list**, not longest-match and not last-match. The JSON below is emitted in the required evaluation order. Substring collisions that will silently mis-bar accounts if the order is changed:

- `20x` / `5x` before `max` (Claude) — otherwise "Max 20x" resolves to the Max 5x row
- `pro 5x` / `pro 20x` before `pro` (Codex)
- `ultra max` / `max` before `ultra` (Google) — both paid tiers are officially named "Google AI Ultra"
- `hobby` / `free` before bare `pro` (Cursor) — the app's own status string is `"Cursor · Free/Pro"` and contains **both**
- `pro max` and `pro+` before `pro` (Kiro) — **this is a live bug today:** `Catalog.cs:795` defines only `power`/`pro+`/`pro`, so a Pro Max account matches `pro` and gets a 5x-too-small bar
- `pro trial` before `pro plus` before `pro` (Qoder) — otherwise a trial account charts as full Pro, 3x too generous
- `pro+` before `pro` (Copilot)
- `coding pro` / `token pro` — **never a bare `pro` for BayesDL**; the two would collide at a 27x error

---

## 4. Data

```json
[
  {"providerType":"claude","planPattern":"max 20x","weeklyTokensMillions":600},
  {"providerType":"claude","planPattern":"max20x","weeklyTokensMillions":600},
  {"providerType":"claude","planPattern":"20x","weeklyTokensMillions":600},
  {"providerType":"claude","planPattern":"team premium","weeklyTokensMillions":350},
  {"providerType":"claude","planPattern":"premium seat","weeklyTokensMillions":350},
  {"providerType":"claude","planPattern":"max 5x","weeklyTokensMillions":350},
  {"providerType":"claude","planPattern":"max5x","weeklyTokensMillions":350},
  {"providerType":"claude","planPattern":"5x","weeklyTokensMillions":350},
  {"providerType":"claude","planPattern":"max","weeklyTokensMillions":350},
  {"providerType":"claude","planPattern":"team standard","weeklyTokensMillions":100},
  {"providerType":"claude","planPattern":"standard seat","weeklyTokensMillions":100},
  {"providerType":"claude","planPattern":"team","weeklyTokensMillions":100},
  {"providerType":"claude","planPattern":"enterprise","weeklyTokensMillions":350},
  {"providerType":"claude","planPattern":"pro","weeklyTokensMillions":100},
  {"providerType":"claude","planPattern":"free","weeklyTokensMillions":0},

  {"providerType":"codex","planPattern":"pro 5x","weeklyTokensMillions":160},
  {"providerType":"codex","planPattern":"pro5x","weeklyTokensMillions":160},
  {"providerType":"codex","planPattern":"5x","weeklyTokensMillions":160},
  {"providerType":"codex","planPattern":"pro 20x","weeklyTokensMillions":600},
  {"providerType":"codex","planPattern":"pro20x","weeklyTokensMillions":600},
  {"providerType":"codex","planPattern":"20x","weeklyTokensMillions":600},
  {"providerType":"codex","planPattern":"enterprise","weeklyTokensMillions":45},
  {"providerType":"codex","planPattern":"edu","weeklyTokensMillions":45},
  {"providerType":"codex","planPattern":"business","weeklyTokensMillions":32},
  {"providerType":"codex","planPattern":"team","weeklyTokensMillions":32},
  {"providerType":"codex","planPattern":"plus","weeklyTokensMillions":32},
  {"providerType":"codex","planPattern":"pro","weeklyTokensMillions":600},
  {"providerType":"codex","planPattern":"go","weeklyTokensMillions":11},
  {"providerType":"codex","planPattern":"free","weeklyTokensMillions":1.5},

  {"providerType":"gemini","planPattern":"ultra max","weeklyTokensMillions":600},
  {"providerType":"gemini","planPattern":"20x","weeklyTokensMillions":600},
  {"providerType":"gemini","planPattern":"max","weeklyTokensMillions":600},
  {"providerType":"gemini","planPattern":"ultra","weeklyTokensMillions":150},
  {"providerType":"gemini","planPattern":"pro","weeklyTokensMillions":30},
  {"providerType":"gemini","planPattern":"plus","weeklyTokensMillions":8},
  {"providerType":"gemini","planPattern":"individual","weeklyTokensMillions":8},
  {"providerType":"gemini","planPattern":"no plan","weeklyTokensMillions":8},
  {"providerType":"gemini","planPattern":"base","weeklyTokensMillions":8},
  {"providerType":"gemini","planPattern":"free","weeklyTokensMillions":8},

  {"providerType":"antigravity","planPattern":"ultra max","weeklyTokensMillions":600},
  {"providerType":"antigravity","planPattern":"20x","weeklyTokensMillions":600},
  {"providerType":"antigravity","planPattern":"max","weeklyTokensMillions":600},
  {"providerType":"antigravity","planPattern":"ultra","weeklyTokensMillions":150},
  {"providerType":"antigravity","planPattern":"pro","weeklyTokensMillions":30},
  {"providerType":"antigravity","planPattern":"plus","weeklyTokensMillions":8},
  {"providerType":"antigravity","planPattern":"individual","weeklyTokensMillions":8},
  {"providerType":"antigravity","planPattern":"no plan","weeklyTokensMillions":8},
  {"providerType":"antigravity","planPattern":"base","weeklyTokensMillions":8},
  {"providerType":"antigravity","planPattern":"free","weeklyTokensMillions":8},

  {"providerType":"kimi","planPattern":"vivace","weeklyTokensMillions":1750},
  {"providerType":"kimi","planPattern":"allegro","weeklyTokensMillions":900},
  {"providerType":"kimi","planPattern":"allegretto","weeklyTokensMillions":330},
  {"providerType":"kimi","planPattern":"advanced","weeklyTokensMillions":330},
  {"providerType":"kimi","planPattern":"moderato","weeklyTokensMillions":92},
  {"providerType":"kimi","planPattern":"intermediate","weeklyTokensMillions":92},
  {"providerType":"kimi","planPattern":"andante","weeklyTokensMillions":17},
  {"providerType":"kimi","planPattern":"basic","weeklyTokensMillions":17},
  {"providerType":"kimi","planPattern":"adagio","weeklyTokensMillions":0},
  {"providerType":"kimi","planPattern":"free","weeklyTokensMillions":0},

  {"providerType":"cursor","planPattern":"ultra","weeklyTokensMillions":250},
  {"providerType":"cursor","planPattern":"teams premium","weeklyTokensMillions":160},
  {"providerType":"cursor","planPattern":"team premium","weeklyTokensMillions":160},
  {"providerType":"cursor","planPattern":"premium","weeklyTokensMillions":160},
  {"providerType":"cursor","planPattern":"pro+","weeklyTokensMillions":150},
  {"providerType":"cursor","planPattern":"pro plus","weeklyTokensMillions":150},
  {"providerType":"cursor","planPattern":"proplus","weeklyTokensMillions":150},
  {"providerType":"cursor","planPattern":"teams","weeklyTokensMillions":120},
  {"providerType":"cursor","planPattern":"team","weeklyTokensMillions":120},
  {"providerType":"cursor","planPattern":"business","weeklyTokensMillions":120},
  {"providerType":"cursor","planPattern":"hobby","weeklyTokensMillions":2},
  {"providerType":"cursor","planPattern":"free","weeklyTokensMillions":2},
  {"providerType":"cursor","planPattern":"pro","weeklyTokensMillions":120},

  {"providerType":"windsurf","planPattern":"max","weeklyTokensMillions":44},
  {"providerType":"windsurf","planPattern":"teams","weeklyTokensMillions":7},
  {"providerType":"windsurf","planPattern":"team","weeklyTokensMillions":7},
  {"providerType":"windsurf","planPattern":"pro","weeklyTokensMillions":7},
  {"providerType":"windsurf","planPattern":"free","weeklyTokensMillions":0.5},

  {"providerType":"copilot","planPattern":"pro+","weeklyTokensMillions":16},
  {"providerType":"copilot","planPattern":"pro plus","weeklyTokensMillions":16},
  {"providerType":"copilot","planPattern":"max","weeklyTokensMillions":46},
  {"providerType":"copilot","planPattern":"enterprise","weeklyTokensMillions":9},
  {"providerType":"copilot","planPattern":"business","weeklyTokensMillions":4.4},
  {"providerType":"copilot","planPattern":"pro","weeklyTokensMillions":3.5},
  {"providerType":"copilot","planPattern":"free","weeklyTokensMillions":0.4},

  {"providerType":"qoder","planPattern":"ultra","weeklyTokensMillions":46},
  {"providerType":"qoder","planPattern":"team","weeklyTokensMillions":6.9},
  {"providerType":"qoder","planPattern":"pro trial","weeklyTokensMillions":1.5},
  {"providerType":"qoder","planPattern":"trial","weeklyTokensMillions":1.5},
  {"providerType":"qoder","planPattern":"pro plus","weeklyTokensMillions":14},
  {"providerType":"qoder","planPattern":"pro+","weeklyTokensMillions":14},
  {"providerType":"qoder","planPattern":"pro","weeklyTokensMillions":4.6},
  {"providerType":"qoder","planPattern":"community","weeklyTokensMillions":0.5},
  {"providerType":"qoder","planPattern":"basic","weeklyTokensMillions":0.5},
  {"providerType":"qoder","planPattern":"free","weeklyTokensMillions":0.5},

  {"providerType":"mimo","planPattern":"max","weeklyTokensMillions":390},
  {"providerType":"mimo","planPattern":"pro","weeklyTokensMillions":180},
  {"providerType":"mimo","planPattern":"standard","weeklyTokensMillions":53},
  {"providerType":"mimo","planPattern":"lite","weeklyTokensMillions":20},
  {"providerType":"mimo","planPattern":"trial","weeklyTokensMillions":1},
  {"providerType":"mimo","planPattern":"payg","weeklyTokensMillions":1},
  {"providerType":"mimo","planPattern":"free","weeklyTokensMillions":1},

  {"providerType":"kiro","planPattern":"power","weeklyTokensMillions":92},
  {"providerType":"kiro","planPattern":"pro max","weeklyTokensMillions":46},
  {"providerType":"kiro","planPattern":"promax","weeklyTokensMillions":46},
  {"providerType":"kiro","planPattern":"pro+","weeklyTokensMillions":18},
  {"providerType":"kiro","planPattern":"pro plus","weeklyTokensMillions":18},
  {"providerType":"kiro","planPattern":"pro","weeklyTokensMillions":9},
  {"providerType":"kiro","planPattern":"free","weeklyTokensMillions":0.5},

  {"providerType":"bayesdl","planPattern":"coding pro","weeklyTokensMillions":110},
  {"providerType":"bayesdl","planPattern":"token pro","weeklyTokensMillions":4},
  {"providerType":"bayesdl","planPattern":"token standard","weeklyTokensMillions":2},
  {"providerType":"bayesdl","planPattern":"token lite","weeklyTokensMillions":0.5},
  {"providerType":"bayesdl","planPattern":"体验包","weeklyTokensMillions":0.5},
  {"providerType":"bayesdl","planPattern":"千万token","weeklyTokensMillions":2.3},
  {"providerType":"bayesdl","planPattern":"免费","weeklyTokensMillions":2.3},

  {"providerType":"amp","planPattern":"gigawatt","weeklyTokensMillions":24},
  {"providerType":"amp","planPattern":"megawatt","weeklyTokensMillions":4},
  {"providerType":"amp","planPattern":"enterprise","weeklyTokensMillions":30},
  {"providerType":"amp","planPattern":"pay-as-you-go","weeklyTokensMillions":6},
  {"providerType":"amp","planPattern":"pay as you go","weeklyTokensMillions":6},
  {"providerType":"amp","planPattern":"payg","weeklyTokensMillions":6},
  {"providerType":"amp","planPattern":"free","weeklyTokensMillions":5},

  {"providerType":"factory","planPattern":"enterprise","weeklyTokensMillions":120},
  {"providerType":"factory","planPattern":"business","weeklyTokensMillions":45},
  {"providerType":"factory","planPattern":"team","weeklyTokensMillions":45},
  {"providerType":"factory","planPattern":"plus","weeklyTokensMillions":45},
  {"providerType":"factory","planPattern":"max","weeklyTokensMillions":90},
  {"providerType":"factory","planPattern":"pro","weeklyTokensMillions":9},

  {"providerType":"warp","planPattern":"build","weeklyTokensMillions":3},
  {"providerType":"warp","planPattern":"max","weeklyTokensMillions":35},
  {"providerType":"warp","planPattern":"business","weeklyTokensMillions":3},
  {"providerType":"warp","planPattern":"enterprise","weeklyTokensMillions":6},
  {"providerType":"warp","planPattern":"free","weeklyTokensMillions":0}
]
```

**Rows: 132. Order is significant — first match wins.**

### Follow-up items surfaced by the synthesis

1. **`Catalog.cs:795` — Kiro is missing `pro max` (46) and `free` (0.5).** A Pro Max account currently matches the `pro` rule and renders a 5x-undervalued bar. Highest-priority fix in this set.
2. **`Catalog.cs` — Codex needs `pro 5x`/`5x` ahead of `pro`**, or every $100 Pro account charts at the $200 value (3.75x over).
3. **Windsurf provider may still parse a retired credit balance.** Prompt credits were retired 19 Mar 2026 and Cascade was retired 1 Jul 2026; `windsurf.com/pricing` now 308-redirects to `devin.ai/pricing`. Worth a separate audit of the provider and its hardcoded URLs.
4. **Copilot Business/Enterprise promo credits (1 Jun – 1 Sep 2026)** inflate observed pools 1.6–1.8x above the encoded standard rate. Expect a visible drop on 1 Sep; the standard rate is the correct durable value.
5. **Anthropic's +50% weekly promo** (if it ever existed) runs through 19 Aug 2026. The corrected Claude figures do *not* include it, so no cliff is expected there.
6. **Re-verify Cursor and Google quarterly.** Cursor changed pricing three times in 2026; Google's Antigravity limits have moved by more than an order of magnitude in nine months.