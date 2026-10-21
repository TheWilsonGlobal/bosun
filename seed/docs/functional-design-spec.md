# MV Ha Long Functional Design Specification

## System architecture

The integrated alarm and monitoring system is built from three BX-2000 controllers in the engine control room, connected to each other and to the operator stations by a redundant network ring. A failure of one network path does not interrupt communication. Each controller carries its own I/O modules, so that the loss of one controller affects only the equipment connected to it.

There are two operator stations in the engine control room, one operator station on the bridge, and one view-only station in the chief engineer's cabin. Only the stations in the engine control room and on the bridge can acknowledge alarms. The system stores all alarm events and operator actions for 90 days in a local database, and older records are overwritten in order. Process values are logged once per second for the trend displays and are kept for 30 days.

## Tag naming convention

Every tag name has the form {SYSTEM}-{EQUIP}-{MEASURE}{NN}, for example ME-EXH-TT01. SYSTEM is the system code of 2 to 5 upper case letters: ME main engine, AE auxiliary engines, BLR boiler, PMS power management, BWTS ballast water treatment and TANK tanks. EQUIP identifies the equipment inside the system, in up to 8 upper case letters and digits. MEASURE is the measurement code: TT temperature, PT pressure, LT level, FT flow, ST speed, ZT position, II current, XS status, XA alarm contact and YS command. NN is a two-digit sequence number from 01 to 99.

A complete tag name has at most 24 characters and is unique within the project. The validation rules reject names that do not follow the pattern. The name of a retired tag is not reused for 12 months, so that old trend data and audit entries cannot be confused with a new signal.

## Signal types, scaling and sensor faults

The system supports analog inputs (AI) and outputs (AO), and digital inputs (DI) and outputs (DO). Analog signals are 4 to 20 mA: 4 mA is the low end of the range and 20 mA the high end, and the value in between is scaled linearly into the engineering unit of the tag. A current below 3.6 mA, or above 21 mA, is treated as a sensor fault, usually a broken wire or a short circuit, and not as a process value.

When a sensor fault is detected the value is marked as bad quality, the last good value is frozen on the displays, and limits are not evaluated on a bad value. A sensor fault alarm of Priority 2 is raised in the same alarm group as the tag. Thermocouples are connected to dedicated thermocouple input modules with cold junction compensation. Pressure signals have a first-order input filter of 1 s; temperature and level signals are not filtered in the controller.

Digital alarm contacts are wired normally energised, so that a broken wire or a lost supply is shown as an alarm, which is the fail-safe direction. Digital inputs work with 24 V DC.

## Configuration change control

All changes to the configuration are made as change sets. A change set moves from draft to validated, approved and applied, or it is rejected. Validation runs the deterministic rules: limit ordering, range, tag naming, group membership and duplicate detection. A change set that fails validation cannot be approved.

The author of a change set may not approve it, which gives a four-eyes check. Change sets that touch tags with safety class SIL or ESD need a second approver who holds the approver role. Changes to the limits or delays of Priority 1 alarms need a written reason in the change set. Change sets proposed by the AI assistant are treated like any other draft: the assistant can never approve or apply them.

Every applied operation is written to the audit log with the user who approved it, the time, the old and the new value, and, for AI-proposed changes, the model and prompt version. In an emergency at sea the chief engineer may apply a change set alone, but the change must be reviewed by a second person within 24 hours.
