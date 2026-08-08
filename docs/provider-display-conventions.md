# Provider Display Conventions

Provider card titles identify what the account is, not what happened to it.

## Title format

- Use `Provider` when there is no active plan.
- Use `Provider · Plan` when a currently active plan is known.
- Preserve an explicit instance name as the provider portion when the user configured one.
- Do not put lifecycle or health status in the title. This includes `expired`, `inactive`,
  `disconnected`, `login required`, `trial ended`, and error text.
- Put actionable status in the card body, quota row, error state, or other dedicated status UI.

Examples:

| State | Title | Card content |
| --- | --- | --- |
| Active Standard plan | `MiMo · Standard` | Current quota windows |
| Expired or no plan | `MiMo` | `Plan expired` or the relevant empty state |
| Login required | `MiMo` | Login-required error/action |

## Enforcement

Provider parsers should emit the provider-only name when entitlement is expired. The provider card
view model also falls back to the configured/default provider name for every snapshot whose
`EntitlementStatus` is `Expired`, preventing stale plan or status text from leaking into the title.
