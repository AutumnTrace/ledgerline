# 2. Money as integer minor units with an explicit ISO 4217 currency

- Status: Accepted
- Date: 2026-11-23

## Context

Floating point cannot represent 0.10 exactly, and rounding errors in a ledger accumulate into amounts
that do not balance. `decimal` is exact for currency amounts, but it still lets code create values
with more precision than the currency has (EUR 10.005), and nothing stops adding euros to yen.
Currencies also differ in precision: EUR and USD have 2 minor digits, JPY has 0, KWD and BHD have 3.

## Decision

- `Money` is a `readonly record struct` of a `long` amount in **minor units** plus a `Currency`.
- `Currency` is a closed set of ISO 4217 codes with their minor-unit exponent. Codes are upper case;
  `eur` and unknown codes are rejected at the edge.
- Arithmetic is `checked` (overflow throws) and refuses to mix currencies.
- Conversion from a major-unit `decimal` rejects extra precision instead of rounding:
  `Money.FromMajor(10.005m, EUR)` is an error, not EUR 10.01.
- The API takes `amountMinor` as an integer and returns both the exact `minor` value and a formatted
  `amount` string for humans (`{"minor": 2500, "currency": "EUR", "amount": "25.00"}`).
- The database stores `amount_minor bigint` and `currency varchar(3)`.

## Consequences

- Sums are exact and fast (integer addition in C# and in SQL), and the zero-sum invariant is an exact
  equality, not a tolerance.
- `long` minor units cover about 92 quadrillion EUR. Request amounts are capped at 10^12 minor units, far
  below the point where sums could overflow.
- Clients must know the currency exponent to display amounts; the formatted `amount` field covers the
  common case.
- Adding a currency is a code change. That is deliberate: a ledger should not silently accept a
  currency nobody has decided to support.

## Alternatives considered

- **`double`.** Rejected outright for money.
- **`decimal` in major units.** Exact, but the precision rule would have to be enforced everywhere, and
  integer columns are smaller and faster to aggregate.
- **A money library.** The surface needed here is small; owning it keeps the rules (no rounding, no
  mixing, checked arithmetic) explicit and tested.
