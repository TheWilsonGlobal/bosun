# MV Ha Long Cause and Effect Notes

## How to read the matrix

The cause and effect matrix lists causes in its rows and effects in its columns. A cause is an alarm condition, written as a tag name and a limit level, for example ME-EXH-TT03 HH. An effect is something the system does in response. The effect codes are:

- A: alarm only, shown on the operator stations according to its priority.
- LR: load reduction request sent to the engine control system.
- SD: automatic slowdown of the main engine to a safe speed.
- ST: shutdown or trip of the equipment.
- SB: start of the standby unit, for example the standby lubricating oil pump or the standby generator.
- I: interlock that blocks a start.

In a cell, X means the effect always happens, T(n) means it happens only after the cause has been active for n seconds, and (O) means the effect can be overridden by the engineer in charge, with a reason that is written to the audit log. Every cause in the matrix needs a configured limit on its tag. A cause without a configured limit is a configuration error and is reported by the validation of the change set.

## Main engine protection

Exhaust gas temperature: an H alarm on a ME-EXH-TT tag (95 degrees C, delay 10 s) gives effect A only. An HH alarm gives LR, which reduces the engine load to 60 % of the maximum continuous rating, after a delay of 2 s. If the temperature of any cylinder deviates by more than 40 degrees C from the average of all cylinders for 30 s, an alarm A is raised; this deviation alarm has no automatic action.

Lubricating oil pressure: an L alarm at 3.5 bar gives A and SB, which starts the standby lubricating oil pump. An LL alarm at 2.5 bar gives SD after 3 s. The engine is shut down (ST) only when two out of three hardwired low-low pressure switches agree, and this trip does not depend on the alarm system.

Jacket cooling water: an H alarm at 90 degrees C gives A, and an HH alarm at 95 degrees C gives SD after 2 s.

Overspeed: a speed above 115 % of rated speed gives ST by a hardwired path that is independent of the alarm system. The effect SD can be overridden by the engineer in charge (O) when the ship must avoid immediate danger, for example in confined waters. The overspeed trip and the two-out-of-three lubricating oil trip cannot be overridden.

## Boiler and steam protection

Boiler water level: an L alarm gives A and starts the standby feed water pump (SB). An LL alarm gives ST of the burner after 5 s, and the burner may only be restarted after the level is back above the L limit. Because a water level that is too low can destroy a boiler in minutes, the LL trip cannot be overridden.

Steam pressure: an H alarm at 8.5 bar gives A. An HH alarm at 9.5 bar gives ST of the burner immediately. The safety valves are set to lift at 10 bar and are independent of the control system.

Flame failure: the burner trips (ST) within 1 s of losing the flame signal. After every flame failure trip the furnace must be purged for 120 s before the burner may be restarted; the interlock I blocks the start during the purge. A second flame failure within 10 minutes locks the burner out until an engineer resets it locally.

## Electrical power and blackout recovery

The power management system (PMS) keeps the generators matched to the load. When a running generator exceeds 85 % load for 30 s, the PMS starts the standby generator (SB), synchronises it and shares the load. When the load of a generator exceeds 95 % for 5 s, the PMS trips the non-essential consumers (accommodation heating, galley and air conditioning) in the first stage of preferential tripping.

A blackout is declared when the main switchboard voltage is below 70 % for 0.5 s. The PMS then starts the first available standby generator, which must be on the bus within 30 s. After that the essential consumers are restarted in steps of 5 s in a fixed order: steering gear first, then lubricating oil pumps, cooling water pumps, fuel oil pumps and last the engine room ventilation. Each step is released only when the bus voltage and frequency are stable. A blackout always creates a Priority 1 alarm in the PMS group.
