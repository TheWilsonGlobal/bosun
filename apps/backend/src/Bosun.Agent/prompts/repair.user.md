The rule check of the draft found problems that must be fixed before a human can review it (repair round {{round}} of {{max_rounds}}):

{{violations}}

Fix them with further propose_* calls (a later call for the same tag and level replaces an earlier one), call validate_draft, then call finish_task. If the engineer's request cannot be met without breaking a rule, call finish_task with outcome clarify and explain which rule blocks it.
