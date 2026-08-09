# Adaptive Controller — Algorithm & Implementation

See [PROJECT.md](PROJECT.md) for the high-level overview and how this fits the XmaX architecture.

## Design Rationale

The backend is the persistent process (service/daemon, survives frontend crashes, always running). Thermal management is safety-critical infrastructure — it cannot depend on the GUI being alive. The backend also has metrics in shared memory and direct hardware access, so adjustments happen with zero IPC overhead.

The frontend only configures the controller and observes its decisions — the backend owns the control loop.

## Architecture

The controller runs on its own thread in the backend. It reads from the shared metrics struct (updated by the metrics poller at 2000ms) and writes TDP/fan directly via the platform layer.

```
Metrics poller (2000ms)          Adaptive controller (500ms–1s)
    │                                    │
    ▼                                    ▼
 Shared metrics ──────────────► Read temp (CPU + GPU), power, load
    │                                    │
    │                          ┌─────────▼──────────┐
    │                          │  Asymmetric smooth  │
    │                          │  PID fan control    │
    │                          │  TDP outer loop     │
    │                          └─────────┬──────────┘
    │                                    │
    │                                    ▼
    │                         Write TDP/fan via platform layer
    │                         Emit auto_tune_adjust event to FE
```

## Algorithm

All three tuning presets share the same core structure — a two-loop controller with asymmetric spike handling. The preset changes the tuning parameters, not the algorithm.

### Two-loop structure

```
┌──────────────────────────────────────────────────┐
│  Outer loop: TDP adjustment (slow, cautious)     │
│                                                    │
│  "How much performance can we sustain at this     │
│   fan level?"                                     │
└────────────────────────┬─────────────────────────┘
                         │ TDP target
                         ▼
┌──────────────────────────────────────────────────┐
│  Inner loop: Fan PID controller (fast)            │
│                                                    │
│  "What fan speed keeps us at target temp with     │
│   this TDP?"                                      │
└──────────────────────────────────────────────────┘
```

The fan is the first line of defense. When temps rise, the fan ramps up. TDP stays untouched as long as the fan can handle it. Only when the fan runs out of headroom (hits its ceiling) does TDP start to come down.

### Pseudocode

```
every tick (1s):
    temp = max(raw_cpu_temp, raw_gpu_temp)

    // --- Asymmetric smoothing (spike absorption) ---
    if temp > smoothed_temp:
        smoothed_temp = lerp(smoothed_temp, temp, smooth_rise_alpha)
    else:
        smoothed_temp = lerp(smoothed_temp, temp, smooth_fall_alpha)

    // --- Inner loop: Fan PID ---
    error = smoothed_temp - target_temp
    integral += error * dt
    integral = clamp(integral, -100, 100)                   // anti-windup
    derivative = (error - prev_error) / dt

    fan_pct = Kp * error + Ki * integral + Kd * derivative
    fan_pct = clamp(fan_pct, fan_min, fan_max)

    // --- Outer loop: TDP adjustment ---
    if fan_pct >= fan_max and smoothed_temp > target_temp:
        tdp_target -= ramp_down_rate * dt                   // gradual
    elif fan_pct < (fan_max * 0.7) and smoothed_temp < target_temp:
        tdp_target += ramp_up_rate * dt                     // slower than down
    // --- Power state TDP ceiling clamping ---
    effective_tdp_max = min(auto_tune.tdp_max_w, power_state.tdp_max_w)
    tdp_target = clamp(tdp_target, TDP_HARDWARE_MIN, effective_tdp_max)

    // --- Immediate safety override (raw temp, no smoothing) ---
    if temp > critical_temp:
        fan_pct = 100
        tdp_target = TDP_HARDWARE_MIN

    write_fan(fan_pct)
    write_tdp(tdp_target)
```

### Asymmetric smoothing (spike handling)

The key to not overreacting to transient temperature spikes:

```cpp
if (raw_temp > smoothed_temp) {
    // Rising: track quickly (α = 0.5, half-life ≈ 1 tick)
    smoothed_temp = 0.5 * raw_temp + 0.5 * smoothed_temp;
} else {
    // Falling: decay slowly (α = 0.05, half-life ≈ 14 ticks)
    smoothed_temp = 0.05 * raw_temp + 0.95 * smoothed_temp;
}
```

At 1s tick rate:
- A spike from 80°C → 92°C is tracked within ~2 seconds
- Recovery from 92°C back to 80°C takes ~28 seconds

**What this achieves:** a 3-second spike to 92°C triggers a mild fan bump (proportional to overshoot) but doesn't touch TDP. If the spike sustains for 10+ seconds, the smoothed temp catches up and TDP gradually adjusts. When the spike passes, the slow decay prevents "all clear, max TDP!" whiplash.

**Important:** the unsmoothed `temp` (max of raw CPU and GPU) is still used for **immediate safety** — if it exceeds `critical_temp` (default 95°C), both fan goes to max and TDP drops immediately, bypassing smoothing. Smoothing is for the comfort zone, not emergencies.

### Why this works

- **Maximize TDP:** TDP only decreases when the fan is saturated AND smoothed temp exceeds target. During normal gaming, the fan handles it — TDP stays at max.
- **Minimize fan:** The PID controller finds the minimum fan speed that maintains target temp. It doesn't overshoot because the integral term only accumulates on sustained error.
- **Handle spikes:** Three layers — asymmetric smoothing absorbs brief spikes, PID proportional term gives mild response, PID integral only acts on sustained error.
- **Gradual limiting:** The outer loop uses ramp rates (watts per second), not step changes. TDP slides, not jumps.

## Tuning Presets — Detailed Behavior

### Silent — fan is the constraint, TDP is the sacrifice

- `fan_max` is user-set and **absolute** (e.g., 40%). The PID never exceeds it.
- Because the fan hits its ceiling early, the outer TDP loop activates sooner and more often.
- `ramp_down_rate` is gentle (1.0 W/s) — TDP eases down, never drops suddenly.
- `ramp_up_rate` is cautious (0.3 W/s) — slow recovery, avoids pushing temps back up.
- Temps may briefly exceed target (thermal throttle territory) during load spikes — the asymmetric smoothing absorbs short spikes, and the system self-corrects as TDP adjusts.

### Default — balanced

- `fan_max` is 100% (full range available).
- The PID finds the minimum fan that maintains target temp at current TDP.
- TDP only comes down if fan saturates (reaches 100%) AND temps still exceed target.
- `ramp_down_rate` is moderate (2.0 W/s), `ramp_up_rate` is slower (0.5 W/s).

### Performance — TDP is the priority, fan is the tool

- `fan_max` is 100% (full range available).
- PID gains are more aggressive (higher Kp) — fan ramps faster to keep temps at target, preserving TDP headroom.
- TDP ramp down is slow (1.0 W/s) — reluctant to give up performance.
- TDP ramp up is fast (2.0 W/s) — eager to restore performance.
- `ramp_up_rate` > `ramp_down_rate` is the opposite of Silent — this preset *wants* to be at max TDP.

## Tuning Preset Parameters

| Parameter | Silent | Default | Performance |
|-----------|--------|---------|-------------|
| `fan_max_pct` | User-set (e.g., 40) | 100 | 100 |
| `fan_min_pct` | 15 | 20 | 25 |
| `Kp` | 1.5 | 2.0 | 3.0 |
| `Ki` | 0.3 | 0.5 | 0.8 |
| `Kd` | 0.1 | 0.1 | 0.2 |
| `ramp_down_rate` (W/s) | 1.0 | 2.0 | 1.0 |
| `ramp_up_rate` (W/s) | 0.3 | 0.5 | 2.0 |
| `smooth_rise_alpha` | 0.5 | 0.5 | 0.5 |
| `smooth_fall_alpha` | 0.05 | 0.05 | 0.05 |

PID gains (Kp, Ki, Kd), ramp rates, and smoothing alphas are **hardcoded per tuning preset** in the backend — not user-configurable. The user selects a tuning preset (`silent`, `default`, `performance`) and configures only `target_temp_c`, `tdp_max_w`, and `fan_max_pct` via the frontend. The algorithm and its internal tuning are opaque to the user.

## Power State TDP Ceiling

The adaptive controller's global `tdp_max_w` (from `config.json` `auto_tune`) is **clamped by the current power state's ceiling**. Each power state (battery, usb_c_slow, usb_c_fast, dc_in) has its own `tdp_max_w` value set by the user via the frontend and stored in `config.json` `power_state_profiles`.

### Clamping formula

```
effective_tdp_max = min(auto_tune.tdp_max_w, power_state.tdp_max_w)
```

The **lower** of the global ceiling and the power state ceiling wins — the adaptive controller can never exceed the power state's hard limit.

### When the ceiling is recalculated

The effective ceiling is recalculated **immediately on power state change** (detected via EC `0x04FE` poll). The global adaptive config is not changed — only the effective ceiling is constrained by the new power state's limit. The controller's `tdp_target` is clamped to the new effective ceiling on the next tick.

### Example

Global adaptive config has `tdp_max_w: 55`. On DC-IN, the power state is configured with `tdp_max_w: 55`:

```
effective_tdp_max = min(55, 55) = 55   // equal, full ceiling available
```

User unplugs to battery (configured `tdp_max_w: 25`). The global adaptive config stays the same:

```
effective_tdp_max = min(55, 25) = 25   // power state ceiling wins
```

The adaptive controller can no longer set TDP ceilings above 25W, constrained by the battery power state's lower limit.

## IPC Interface

### Configuration (FE → BE via `set_auto_tune`)

The adaptive controller is a **global singleton** — one config, stored in `config.json` `auto_tune`. It is mutually exclusive with user profiles (radio-button model): activating adaptive deactivates the current profile, and selecting a profile deactivates adaptive. Exactly one is always active after the user's first selection. The controller operates across all power states.

| Parameter | Type | Purpose |
|-----------|------|---------|
| `tuning` | string | `"silent"`, `"default"`, or `"performance"` — selects the hardcoded tuning preset |
| `target_temp_c` | int | Preferred max temperature (e.g., 85°C) |
| `tdp_max_w` | float | Ceiling — clamped by power state's `tdp_max_w` (see Power State TDP Ceiling) |
| `fan_max_pct` | int | Fan ceiling (primary control for Silent tuning preset) |

Sending `set_auto_tune` activates the adaptive controller and deactivates the active profile. Adaptive can only be deactivated by selecting a profile — there is no direct disable action. PID gains, ramp rates, and smoothing alphas are hardcoded per tuning preset and not user-configurable.

### State read (FE → BE via `get_auto_tune`)

Returns: `{active, tuning, target_temp_c, tdp_max_w, effective_tdp_max_w, fan_max_pct}`

`active` indicates whether adaptive is the current active mode (true) or a profile is active (false). `effective_tdp_max_w` is the post-clamping ceiling after applying the current power state's limit — the actual maximum the controller can set.

### Events (BE → FE)

| Event | Payload | When |
|-------|---------|------|
| `auto_tune_adjust` | `{tuning, tdp_w, fan_pct, smoothed_temp_c, effective_tdp_max_w, reason}` | When TDP or fan values actually change (not every tick — suppress if unchanged) |
| `auto_tune_state` | `{active}` | On every mode transition — adaptive became active (profile deselected) or inactive (profile selected) |

#### `reason` values

The `reason` field in `auto_tune_adjust` is a short machine-readable string explaining *why* the controller made the adjustment. The frontend can map these to user-facing labels.

**Fan adjustments:**

| Value | Meaning |
|-------|---------|
| `fan_up` | PID increased fan — smoothed temp above target |
| `fan_down` | PID decreased fan — smoothed temp below target, easing off |

**TDP adjustments:**

| Value | Meaning |
|-------|---------|
| `tdp_up` | Outer loop restoring TDP — fan has headroom, temps below target |
| `tdp_down` | Outer loop reducing TDP — fan saturated, temps still above target |

**Overrides:**

| Value | Meaning |
|-------|---------|
| `critical_temp` | Raw temp exceeded safety limit — immediate max fan + min TDP |
| `sensor_fail` | Sensor reads failed >5s — thermal safety fallback applied (min TDP, max fan) |

**Example payloads:**

```json
{
  "type": "event",
  "event": "auto_tune_adjust",
  "data": {
    "tuning": "default",
    "tdp_w": 42.0,
    "fan_pct": 65.0,
    "smoothed_temp_c": 83,
    "effective_tdp_max_w": 45,
    "reason": "fan_up"
  }
}
```

```json
{
  "type": "event",
  "event": "auto_tune_adjust",
  "data": {
    "tuning": "silent",
    "tdp_w": 25.0,
    "fan_pct": 40.0,
    "smoothed_temp_c": 87,
    "effective_tdp_max_w": 25,
    "reason": "tdp_down"
  }
}
```

## Safety Invariants (hardcoded, not configurable)

- TDP never exceeds hardware max (120W on Strix Halo)
- Fan never drops below minimum safe speed when temp > 70°C
- If sensor reads fail for >5s, apply thermal safety fallback (min TDP, max fan)
- Raw temperature exceeding `critical_temp` (default 95°C) triggers immediate max fan + min TDP, bypassing smoothing

## State Machine

```
                  set_profile
inactive ◄────────────────────────► active
    │                                  │
    │     set_auto_tune(tuning=...)    │
    └──────────────────────────────────┘
```

Adaptive is either **active** (controlling hardware) or **inactive** (a profile controls hardware). Mutually exclusive with profiles — exactly one is always active. There is no direct disable action: adaptive is deactivated only when a profile is selected, and reactivated only when a tuning preset is selected. Before the user's first selection (fresh install), adaptive is inactive and no profile is active.

## Implementation Notes

### Source file

`backend/src/adaptive.cpp` — implements the controller thread, reads shared metrics, writes TDP/fan via the `Platform` interface.

### Integration points

- **Metrics poller** writes to shared state at 2000ms. The adaptive controller reads from the same shared state (mutex-protected) at its own tick rate (1s).
- **Transport server** dispatches `set_auto_tune` / `get_auto_tune` commands to the controller's config struct (mutex-protected).
- **Event emission** — the controller pushes `auto_tune_adjust` and `auto_tune_state` events to all connected frontend clients via the transport server.

### Thread safety

- Config struct: protected by a shared mutex (`std::shared_mutex`). FE writes config, controller reads it. Contention is minimal (config changes are rare).
- Metrics struct: the controller reads metrics written by the metrics poller. Same mutex as existing shared state.
- Hardware writes: TDP and fan writes go through the `Platform` interface, which serializes access internally.
