# QuotaLens handoff

## Current status

The completed provider, launch, connection, plan-identity, and documentation work is preserved in the working tree. The attempted token-efficiency implementation from the latest discussion was reverted completely; no token-efficiency UI or ranking changes are currently implemented.

The last full validation before the final documentation-only edits passed all 800 tests with no warnings. The Markdown-only follow-up passed `git diff --check` and local-link validation.

## Completed work awaiting/covered by the current commit

- Provider launch and connection behavior is represented by shared, provider-agnostic metadata and actions rather than dashboard provider-name branches.
- App, CLI, and Web sources have strict explicit selection, source-specific launch behavior, and shared connection helpers.
- Gemini App supports Antigravity and Antigravity IDE detection from one App source, background startup, and readiness verification.
- Launch labels/icons follow the effective source and executable; CLI launch uses a terminal.
- Provider titles are composed centrally from structured provider and plan identity. Grok and Kimi plan enrichment no longer concatenate display titles inside individual providers.
- The English and Chinese READMEs are product-facing. Configuration, provider behavior, troubleshooting, architecture, build, and test details live under `docs/`.
- Outdated temporary planning/change-log documents were removed and the user/developer guides were refreshed.

## Latest product discussion — not implemented

The user no longer finds the dashboard usage bar chart or the sort selector below it useful. They want the product to answer one simple question:

> Which provider should I use for token efficiency, considering that token allowances reset?

This was a request for discussion, not authorization to change the app. Do not implement it until the recommendation semantics are agreed with the user.

The key unresolved decision is what “token efficiency” means:

1. **Nearest reset:** use remaining quota that will expire soonest.
2. **Fastest refill:** prefer the provider whose quota replenishes most frequently.
3. **Waste pressure:** combine estimated remaining tokens with time until reset, prioritizing the largest amount of capacity at risk per hour.

These can produce different recommendations. The next conversation should use concrete examples (for example, a 27% weekly pool, a 60% five-hour pool, and a 100% five-hour pool) and agree on the expected winner before choosing an algorithm or changing the UI.

Once the meaning is settled, discuss whether provider cards should keep a fixed order, follow the same recommendation score, or remain independently sortable. The only agreed UI direction so far is to simplify the dashboard by removing the bar chart and visible sort choices in favor of a direct answer.

## Suggested next step

Discuss two or three example quota/reset scenarios with the user and write the expected recommendation for each. Turn those examples into policy tests only after the user confirms the behavior.
