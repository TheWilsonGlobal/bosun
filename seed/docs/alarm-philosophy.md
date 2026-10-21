# MV Ha Long Alarm Philosophy

## Purpose and principles

This document defines how alarms are designed, prioritised and presented on MV Ha Long, a product tanker with one medium-speed main engine and three auxiliary generator sets. It applies to every process alarm configured in the integrated alarm and monitoring system. The goal is simple: an operator who sees an alarm must be able to do something useful about it.

Every alarm has to pass four tests. It must be relevant, meaning it concerns equipment the operator is responsible for. It must be actionable, meaning a defined response exists. It must be unique, meaning no two alarms announce the same event. It must be timely, meaning it is raised early enough to act on but not so early that it is noise. An alarm that fails any of the four tests is removed or downgraded to a log entry.

The design targets for alarm load are an average of at most 2 alarms per operator per 10 minutes in steady running, and at most 10 alarms in the first 10 minutes after a plant upset. A tag that raises more than 5 alarms per day on 3 days in a row is called a bad actor. Bad actors are reviewed at the alarm rationalisation meeting, which is held every 6 months and after every sea trial.

## Priorities and alarm groups

There are three priorities. Priority 1 is Critical: red, with a continuous horn, and the operator must respond within 1 minute. Priority 2 is Warning: amber, with an intermittent horn, and the operator must respond within 10 minutes. Priority 3 is Advisory: white, no horn, and the alarm is only logged and reviewed at the next watch handover. Alarms at level H and level L are Priority 2. Alarms at level HH and level LL are Priority 1. Status alarms from digital inputs are Priority 2 unless the cause and effect matrix says otherwise.

Alarms are collected into alarm groups. A group name has the form SYSTEM-AREA, for example ME-EXH for main engine exhaust gas, ME-LO for main engine lubricating oil, AE-EXH for auxiliary engine exhaust, BLR-WTR for boiler water and TANK-FO for fuel oil tanks. Group names are upper case and case-sensitive. A group is the unit of horn silencing and of display filtering on the operator stations. A tag belongs to exactly one group, and a group holds at most 40 tags, so that a single group never floods the alarm list.

## Limit levels and hysteresis

Each analog tag can have up to four limits: LL (low-low), L (low), H (high) and HH (high-high). They must be ordered LL < L < H < HH and must lie inside the measuring range of the tag. A limit that is not needed is simply left out; there are no dummy limits at the end of the range.

Hysteresis is the dead band that stops an alarm from chattering around its limit. An active high alarm clears only when the value falls below the limit minus the hysteresis, and a low alarm clears when the value rises above the limit plus the hysteresis. The defaults are 2 % of the measuring span for temperature, 1 % of the span for pressure, 3 % of the span for level and 2 % of the span for flow. Example: a jacket water temperature tag with a span of 120 degrees C and an H limit of 90 degrees C has a hysteresis of 2.4 degrees C, so the alarm clears below 87.6 degrees C.

Changes to the limits of tags with safety class SIL or ESD need a second approver, as described in the functional design specification.

## Delay times

The delay is the time a value must stay beyond its limit before the alarm is raised. It filters out short spikes. The defaults per measured quantity are:

| Measured quantity | Default delay for H and L |
| --- | --- |
| Pressure (PT) | 2 s |
| Lubricating oil and cooling water temperature (TT) | 5 s |
| Exhaust gas temperature (ME-EXH-TT) | 10 s |
| Level (LT) | 30 s |
| Flow (FT) | 5 s |
| Speed (ST) | 1 s |
| Digital status (DI) | 1 s |

Level alarms use the longest delay of 30 s because the liquid surface moves with the ship's motion, and a short delay would raise an alarm on every roll. Pressure alarms use 2 s because pressure changes are fast and a real loss of pressure must be shown quickly. The HH and LL limits of temperature tags use a delay of 2 s, so that protective actions are not held back by the filter. A delay longer than 15 s is not accepted for any tag without a written justification in the change set.

## Exhaust gas temperature delay

The reference configuration for the main engine exhaust gas temperature tags ME-EXH-TT01 to ME-EXH-TT08 is an H limit of 95 degrees C with a delay of 10 s, in the alarm group ME-EXH. The delay is 10 s and not the 5 s used for other temperatures for three reasons.

First, thermocouple lag. The sensors are sheathed Type K thermocouples in a thermowell. In flowing exhaust gas their time constant is about 4 to 6 s, which means that after a step change in gas temperature the reading reaches only about 63 % of the step after one time constant and about 86 % after two. A 5 s delay is no longer than the sensor's own response time, so it filters almost nothing: the timer starts on a reading that is still moving. A 10 s delay is about two time constants, which is long enough to confirm that the gas temperature really is above the limit, and long enough to ignore a short excursion.

Second, nuisance alarms during load changes. When the engine load changes by more than 10 % within a minute, for example during manoeuvring or when the turbocharger accelerates, the individual cylinder exhaust temperatures overshoot their steady-state value by 10 to 15 degrees C for roughly 5 to 8 s. In the sea trial of revision A the 5 s delay produced 14 nuisance H alarms during 4 hours of manoeuvring. With the 10 s delay the same manoeuvres produced none. Nuisance alarms teach operators to ignore the alarm list, which is the worst outcome for a protection system.

Third, the extra wait costs no protection. Damage to exhaust valves and turbocharger blades from over-temperature develops over minutes, not seconds. The H alarm is an early warning with a response time of 10 minutes (Priority 2). The real protection is the HH limit, which requests a load reduction after a delay of only 2 s, as defined in the cause and effect notes. Waiting 5 s longer on the H alarm therefore does not weaken the protection of the engine.

The delay must not be shortened below 10 s on ME-EXH-TT tags unless the manoeuvring test is repeated and the nuisance alarm count is recorded in the change set.

## Acknowledgement and shelving

An alarm moves through four states: active and unacknowledged (flashing), active and acknowledged (steady), cleared but unacknowledged (flashing, no horn), and cleared. Acknowledging an alarm at any operator station silences the horn of its group. A Priority 1 alarm that is still unacknowledged after 5 minutes sounds the horn again and is repeated on the bridge repeater panel.

An operator may shelve a Priority 2 or Priority 3 alarm when the equipment is known to be faulty or under maintenance. A shelved alarm is hidden from the alarm list but stays on a permanent shelved-alarms list. Shelving needs a reason text, lasts at most 8 hours, and ends by itself when the time is up. Priority 1 alarms can never be shelved. While the main engine is stopped (the engine-stopped signal true for more than 60 s) its Priority 2 temperature alarms are suppressed automatically and come back when the engine starts.

## What this document does not cover

This document is limited to the configuration of process alarms in the integrated alarm and monitoring system. It deliberately does not cover the following topics, and no other document in this project's document set covers them either:

- Bridge navigational watch alarm system (BNWAS) settings and timings.
- Fire detection and fire-extinguishing alarms and their delays.
- Cybersecurity and remote-access policy.
- Crew manning, watch schedules and training.
- Calculations for safety integrity levels (SIL) and proof-test intervals of safety instrumented functions.
- Class society approval procedures and fees.
- Operating procedures of the ballast water treatment system.
- Commercial and charter-party matters.

For these topics refer to the owner's own manuals and the relevant class society rules, which are not part of the project documents.
