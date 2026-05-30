# Alternative Habitability Model
BepInEx plugin for Solar Expanse with three independently-toggleable model replacements:

- **[Alternative Swing Model](#alternative-swing-model)** — replaces the vanilla day/night temperature swing with a physically-grounded asymmetric model.
- **[Alternative Mirror Model](#alternative-mirror-model)** — replaces the vanilla mirror strength formula with a physically realistic model where mirrors only affect their own planetary system.
- **[Alternative Scaling Model](#alternative-scaling-model)** — replaces the exponential atmosphere/ocean mass scaling with linear relationships using direct multipliers.

Additional model replacements may be added in the future.

> This project was developed with LLM assistance.

## Simulator

Try the model live: [Terraforming Simulator](https://lazyranma.github.io/SETerraformingSimulator/altswing_altmirror.html)

## Alternative Swing Model

The main motivation for this model is to make Venus and Mercury terraformable while staying close to how real physics works.

### Vanilla model issues

1. **Mirrors and shades are ignored.** The swing depends purely on orbital distance (`1/√d_AU`). Adding shades reduces solar flux and lowers the average temperature, but the day/night swing doesn't change at all.

2. **Rotation scaling has no ceiling.** The swing grows with `√(rotationPeriod)`, so slowly rotating bodies have absurdly large swings.

3. **Swing is symmetric around the wrong center.** The game labels the radiative equilibrium point as "average" temperature, but it's not the true mean of the day/night cycle. In reality, the night side plummets far below equilibrium while the day side is capped by the subsolar ceiling — the swing is naturally asymmetric.

### How the model works

Key improvements over vanilla:

- **Mirrors reduce the swing** — half their output illuminates the night side, bringing it closer to the day.
- **Shades reduce the swing** — less total flux means smaller day/night extremes.
- **Atmosphere transports heat** — winds carry energy from day to night.
- **Min/max temperatures have physical limits** — on slow rotators, a night floor prevents collapse below the planetary baseline and the day ceiling is capped by the subsolar limit.
- **Swing is naturally asymmetric** — the cold side drops ~1.8× more than the hot side rises.
- **True mean temperature** — Option to change the average temperature to the actual (T_hot+T_cold)/2, not the equilibrium point (disabled by default).
- **Venus and Mercury are terraformable** — without thick hydrogen-oxygen atmosphere, but beware of runaway greenhouse effect.

### 1. Heat capacity calculation

Total thermal inertia is computed from three components:

```
rockHC  = BaseRockHC × √P_rot                                         [J/m²·K]
depth   = min(NormalHeatDepth × √P_rot, (water^waterScaling × 1000 + surface × minSurfCov)/surface )  [m]
oceanHC = waterHCParam × depth × coverage                             [J/m²·K]
atmHC   = (ΣgasCp × m_gas/M_atm × P × 101325 ) / g × atmFraction      [J/m²·K]

totalHC = rockHC + oceanHC + atmHC
```

- `BaseRockHC` (configurable, default 50000 J/m²·K, replaces vanilla per-planet variable HC): rock/regolith heat capacity at 1-day rotation. Reflects dry, airless regolith like Mars/Luna. On water-rich terraformed planets the true surface HC is higher, but only for the thin surface layer — the underlying rock is unchanged — and ocean HC dominates the total, so the discrepancy can be ignored.
- Rock HC scales with `√P_rot` because the diurnal thermal skin depth penetrates deeper on slow rotators — the thermal wave has more time to soak into the subsurface.
- Ocean HC uses `NormalHeatDepth` (configurable, default 0.5 m) as the 1-day mixing depth, also scaled by `√P_rot`.
- Atmosphere HC follows the vanilla game formula unchanged.

### 2. Radiative cooling timescale

```
τ_rad = totalHC / (4σ T_eq³)     [seconds]
```

How fast the surface relaxes toward local radiative equilibrium.

### 3. Rotation factor

```
f_rot = 1 − exp(−P_rot / (2 τ_rad))
```

Fast rotator (τ_rad ≫ P_rot): `f_rot → 0` — neither side equilibrates, uniform temperature.\
Slow rotator (τ_rad ≪ P_rot): `f_rot → 1` — each side approaches its vacuum limit.

### 4. Flux splitting

Mirrors split their output: fraction `r` to the night side, `1−r` to the day side:

```
baseFlux  = L / (4πd²) × (1 − albedo)
flux_day   = baseFlux × (1 + M × (1−r)) × (1−S)
flux_night = baseFlux × (M × r) × (1−S)
```

Where `M = mirrorsStrength`, `S = shadesStrength`, `r = MirrorRedist` (configurable, default 0.5).

### 5. Hemisphere equilibria

```
T_eq        = (absorbed / 4σ)^0.25            ← global, with mirrors+shades
T_eq_day    = (flux_day / 4σ)^0.25            ← day hemisphere
T_eq_night  = (flux_night / 4σ)^0.25          ← night hemisphere
```

### 6. Illumination contrast

```
T_hot_raw  = max(T_eq_day, T_eq_night)
T_cold_raw = min(T_eq_day, T_eq_night)
contrast   = (T_hot_raw⁴ − T_cold_raw⁴) / (T_hot_raw⁴ + T_cold_raw⁴)
eff_day   = 0.414 × contrast     ← 4^0.25 − 1, hard radiative ceiling
eff_night = 0.75  × contrast     ← rough estimation (configurable)
```

0 = uniform (no swing possible), 1 = fully one-sided (full swing).

This 0.75 rough estimation gives good night temperatures for Mercury (ignoring the Newton correction bug, see below), but Mars nights come out almost 100 K colder than they should. A more accurate model exists but is incompatible with the current temperature model — particularly the greenhouse effect — and may be revisited if the temperature model is ever replaced.

### 7. Vacuum extremes

Both hemispheres use the global `T_eq` as their radiative baseline:

```
T_dayRaw   = T_eq × (1 + eff_day   × f_rot)
T_nightRaw = T_eq × (1 − eff_night × f_rot)
```

Flux splitting works entirely through contrast: by equalizing the hemisphere fluxes, mirrors lower the contrast, pulling both sides toward the same `T_eq` and narrowing the swing.

### 8. Greenhouse boost (spatially uniform)

```
ΔT_gh = T_atm − T_eq
T_day_gh   = T_dayRaw   + ΔT_gh
T_night_gh = T_nightRaw + ΔT_gh
```

> **Note on vacuum worlds:** The game's temperature model overshoots `T_atm` by ~25% for airless bodies (due to a bug in the Newton correction). The swing model works with whatever `T_atm` the game provides — there is no easy correction short of replacing the entire temperature model, and the bug actually masks another major problem in the vanilla model.

### 9. Atmospheric heat transport

```
columnMass = P / g              [kg/m²]
capacity   = columnMass × P_rot [kg·d/m²]
f_trans    = min(capacity/(capacity + 100k), transportPower / (absorbed + transportPower))
```

`transportPower = TransportPower × (P / 101325)` — the atmosphere's finite ability to move heat, capped at ~250 W/m² per atm for N₂/O₂. More mass × more time increases the raw capacity, but the power cap prevents physically impossible perfect mixing on slow rotators.

### 10. Mix toward mean

```
T_hot  = T_day_gh   + (T_atm − T_day_gh)   × f_trans
T_cold = T_night_gh + (T_atm − T_night_gh) × f_trans
```

### 11. Recalculate temperature and swing

```
temperature        = (T_hot + T_cold) / 2 + minKelvin   ← true mean (°C)
temperatureSwings  = (T_hot − T_cold) / 2               ← half-range (K)
```

### Variables

| Symbol | Meaning |
|---|---|
| `T_eq` | Equilibrium temperature (blackbody) |
| `T_atm` | Greenhouse-warmed mean before recalculation (K) |
| `totalHC` | Total thermal inertia of surface + ocean + atmosphere (J/K·m²) |
| `P` | Atmospheric pressure (Pa) |
| `P_rot` | Day/night cycle length (days) |
| `waterScaling` | Exponent for water amount → depth scaling |
| `mirrorsStrength` | Summed mirror amplification factor |
| τ_rad | Radiative cooling timescale |

### Numerical examples

| Body | T_eq | T_atm | T_avg | T_min | T_max | Flux (W/m²) | f_rot | f_redist | H₂O vapor % |
|------|------|-------|-------|-------|-------|-------------|-------|----------|-------------|
| Mercury (vacuum) | +164 °C | +273 °C | +200 °C | −55 °C | +454 °C | 8,266 | 1.00 | 0.00 | – |
| Mercury (terraformed)¹ | −24 °C | +13 °C | −3 °C | −61 °C | +54 °C | 868 | 0.52 | 0.24 | 10.3% |
| Venus (default) | −44 °C | +390 °C | +389 °C | +386 °C | +392 °C | 624 | 0.78 | 0.97 | – |
| Venus (terraformed) | −26 °C | +13 °C | −2 °C | −54 °C | +50 °C | 846 | 0.66 | 0.25 | 8.4% |
| Earth (default) | −18 °C | +21 °C | +17 °C | +5 °C | +30 °C | 966 | 0.10 | 0.09 | 2.5% |
| Moon² (vacuum) | −3 °C | +65 °C | +20 °C | −134 °C | +174 °C | 1,211 | 0.98 | 0.00 | – |
| Moon (terraformed) | −33 °C | +13 °C | +11 °C | +5 °C | +18 °C | 755 | 0.06 | 0.25 | 1.0% |
| Mars (default) | −63 °C | −63 °C | −92 °C | −194 °C | +9 °C | 440 | 0.83 | 0.00 | 0.6% |
| Mars (terraformed) | −38 °C | +13 °C | +12 °C | +7 °C | +16 °C | 699 | 0.07 | 0.22 | 1.0% |

¹ Terraformed: 1 atm (80% N₂ / 20% O₂ by mass, not counting water vapour), 105% ideal water. Flux and T_eq are the required values to sustain ~13 °C T_atm, not nominal at that distance. MirrorRedist = 0.5. 


² The Moon has a 24 h rotation period in-game.

## Alternative Mirror Model

The goal is to replace the vanilla formula — which is physically unrealistic — with a model that respects real physics.

| | Formula |
|---|---|
| Vanilla | `strength = 0.216 / (d_mirror² × d_diff²) × count` |
| AltMirror | `strength = A / (π × R_planet²) × count` |

The model assumes mirrors are close enough to the target that all collected light hits. In practice this means the mirror must be within the same planetary system — the plugin enforces this by ignoring mirrors around other planets or in solar orbit. Mirror area A is configurable (`MirrorAreaMkm2`, default 40 million km²).

**Why 40M km²?** This value is calibrated so that 1 mirror at Mars produces roughly the same flux boost as 1 vanilla mirror at Mercury's orbit targeting Mars (~490 W/m² additional), preserving the base-game terraforming difficulty for Mars.

**What the formula means:** π×R² is the planet's cross-sectional area that already collects light from the Sun. A mirror of area A adds its own collecting area to that disc. `strength = A / (πR²)` is simply the mirror's collecting area as a fraction of the planet's cross-section. One 40M km² mirror at Mars (cross-section 36M km²) adds 110% — effectively more than doubling the light-collecting disc.

### Key terms

- **β (beta)** — how many times wider the Sun's reflected image is compared to the planet. `β = (R_sun × dist_mirror→target) / (R_planet × dist_Sun→mirror)`. β < 1 means all light hits; β > 1 means light spills past the edges.
- **Étendue** — a conserved quantity in optics that sets the minimum beam spread. It is why the Sun's image grows with distance, and why you cannot focus sunlight tighter than the Sun's angular size allows.

### Physical motivation

The vanilla formula has two major errors:

1. **The `1/d_mirror²` term wrongly favours mirrors close to the Sun.** The vanilla meta is to build mirrors at the innermost available orbit — Mercury is strong, and Solar orbit (0.01 AU) is absurdly so: a single one can vaporise the ice of Jupiter's moons from across the solar system. In reality, étendue conservation cancels the mirror's distance to the Sun — but only when β > 1 (spillover regime). A mirror at 0.1 AU collects 100× more flux, but the Sun subtends a 10× larger angle, so the beam covers 100× the area — the planet receives the same power per m² of mirror. When the mirror is close enough that all light hits (β < 1), being nearer the Sun does help, but the gain is modest compared to reducing the mirror-to-target distance.

2. **The `d_diff` term uses orbital radius difference instead of the real 3D mirror-to-planet distance.** Two bodies in the same orbit can be on opposite sides of the star. The étendue-limited 1/D² falloff depends on the actual mirror-to-target vector length, which varies with orbital phase.

### Cross-planet mirrors

Mirrors only affect bodies in their own planetary system. A mirror at Earth's orbit targeting Mars is ignored, and vice versa. This follows from the étendue limit: at interplanetary distances the Sun's image is hundreds of times larger than the target planet, so virtually all reflected light misses. Some examples:

| Mirror at | Targeting | Works? |
|---|---|---|
| Earth orbit | Earth | ✓ |
| Earth orbit | Luna | ✓ |
| Luna orbit | Earth | ✓ |
| Earth orbit | Mars | ✗ |
| Earth orbit | Towed asteroid at Earth | ✓ |
| Solar orbit | Anything | ✗ |

### Assumptions & simplifications

1. **All reflected light hits the target in the same planetary system.** In reality, a mirror at Earth's L2 (~1.5M km) has β ≈ 1.1 (slight spill). Moons and low orbits are well within the all-light-hits regime (β ≪ 1).
2. **Constant effective area.** Real mirrors may depend on orbital position, illumination angle, etc. The plugin assumes each mirror always produces the same flux. Think of mirrors as being on polar orbits or L2 halo orbits, or a single "mirror" is actually a distributed system, or A is just the average effective area over time.

### Per-mirror strength examples

With `MirrorAreaMkm2 = 40` (default):

| Target | Radius (km) | πR² (M km²) | Strength per mirror |
|---|---|---|---|
| Earth | 6,378 | 127.8 | +31.3% |
| Mars | 3,396 | 36.2 | +110.4% |
| Titan | 2,575 | 20.8 | +192.1% |
| Venus | 6,052 | 115.1 | +34.8% |
| Ceres | 455 | 0.65 | +6,153%³ |

³ Yes, this would fry Ceres. Reduce `MirrorAreaMkm2` in config and use Teddit patcher to make mirrors cheaper if you want to use mirrors on it. A better solution for small targets may come later.

## Alternative Scaling Model

Disabled by default.\
This model replaces the game's exponential pressure and water formulas with linear
relationships. Earth's atmosphere and ocean masses are used as reference
points, so `GasScaling=1` and `WaterScaling=1` produce the same pressure
and water score as vanilla for Earth with default deposits.

```
pressure                    = mass × GasScaling × gasNorm × 1000 × g / area / 101325
CurrentScaledWaterAmount    = mass × WaterScaling × waterNorm
ScaledDownIdealWaterAmount  = IdealWaterAmount / (WaterScaling × waterNorm)
```

Set `GasScaling` to 2.0 to double the pressure at the same mass; 0.5 to
halve it. `WaterScaling` works the same way.

### Deposit rescaling

Because the linear model would produce different pressures and water scores
than vanilla, deposit amounts are rescaled for *new games* when
`ScaleDeposits` is enabled (default on).
For each planet or moon, gas and water deposit amounts are adjusted so the
linear model produces the same pressure and water score as vanilla.

If you want custom deposit amounts, use Teddit patcher and disable
`ScaleDeposits`.

To convert an *existing save* to the linear model:

1. Enable `ScaleDepositsOnLoadingSave` in the config.
2. Load your save — deposits will be rescaled on load.
3. Save the game.
4. Disable `ScaleDepositsOnLoadingSave`.

## Configuration

On first launch a config file is generated at `BepInEx/config/com.lazyranma.althabitabilitymodel.cfg`:

### [Swing] section

| Key | Type | Default | Description |
|---|---|---|---|
| `AlternativeSwingModel` | bool | true | Enable the alternative temperature swing model. |
| `UpdateAverageTemperature` | bool | false | When enabled, average temperature = (Min+Max)/2. |
| `MirrorRedist` | double | 0.5 | Fraction of mirror output to night side (0–1). |
| `TransportPower` | double | 250 | Max atmospheric transport at 1 atm N₂/O₂ (W/m²). |
| `NormalHeatDepth` | double | 0.5 | Ocean mixing depth at 1-day rotation (m). |
| `NightFloor` | double | 0.75 | Fractional cold-side drop. |
| `BaseRockHC` | double | 50000 | Rock HC at 1-day rotation (J/m²·K). |

### [Mirror] section

| Key | Type | Default | Description |
|---|---|---|---|
| `AlternativeMirrorModel` | bool | true | Enable the alternative mirror model. |
| `MirrorAreaMkm2` | double | 40 | Mirror area in million km². |

### [Scaling] section

| Key | Type | Default | Description |
|---|---|---|---|
| `AlternativeScalingModel` | bool | false | Enable the alternative scaling model. |
| `ScaleDeposits` | bool | true | Rescale initial deposits for the linear model (new games only). |
| `ScaleDepositsOnLoadingSave` | bool | false | Rescale deposits on save load. Enable, load, save, disable. |
| `GasScaling` | double | 1.0 | Multiplier for linear pressure. |
| `WaterScaling` | double | 1.0 | Multiplier for linear water. |

## Installation

1. Install BepInEx — Follow the [BepInEx setup guide](https://docs.bepinex.dev/articles/user_guide/installation/index.html).
2. Unzip `AlternativeHabitabilityModel_vX.Y.Z.zip` to `BepInEx/plugins/`.

## Build

```
dotnet build -c Release
```

Requires `SOLAR_EXPANSE_DIR` environment variable pointing to the game installation, or edit the `GameDir` property in `AlternativeHabitabilityModel.csproj`.
