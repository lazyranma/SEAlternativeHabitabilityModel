# Changelog

## v0.4.0: Alternative Scaling Model Retired

- Retired the **Alternative Scaling Model** — the vanilla game now uses a similar linear pressure and
  water model, so the replacement is no longer needed.

## v0.3.3: Alternative Composition Model Fix

- Fixed the **Alternative Composition Model** interfering with farm construction requirements and
  mission completion checks. These checks now use the vanilla oxygen mass-fraction requirement,
  while the model continues to calculate habitability using partial pressures.

## v0.3.2: Support for game version 0.26.7.3.9

- Added support for game version 0.26.7.3.9.
- Backwards compatible with version 0.26.6.1.10.

## v0.3.1: Alternative Mirror Model Update

- **Alternative Mirror Model:** allows entering fractional allocated mirror counts to fine-tune
  mirror power.

## v0.3.0: Two New Models

- Added the **Alternative Composition Model** — replaces the vanilla breathability check, which only
  tests whether O₂ is at least 20% of atmosphere mass, with a partial-pressure-based model that
  asks: can humans actually breathe this air?
- Added the **Alternative Photochemistry Model** — hydrogen and methane are not stable in an oxygen
  atmosphere. UV light from the star slowly breaks them down, and if the planet gets hot enough
  they ignite and burn.

## V0.2.0: Alternative Scaling Model

- Added the **Alternative Scaling Model**, disabled by default — replaces the game's exponential
  pressure and water formulas with linear relationships. Earth's atmosphere and ocean masses are
  used as reference points, so `GasScaling=1` and `WaterScaling=1` produce the same pressure and
  water score as vanilla for Earth with default deposits.

## v0.1.1: Removed debug logging

- Removed the debug logging.

## v0.1.0: Initial release

- Initial release.
