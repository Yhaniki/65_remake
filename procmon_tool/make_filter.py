#!/usr/bin/env python
"""Generate a ProcMon config (.pmc) that captures ONLY the target process
and DROPS all other events (so the backing file stays tiny).

Usage: python make_filter.py <out.pmc> [process_name]
"""
import sys
from procmon_parser import Rule, Column, RuleRelation, RuleAction, dump_configuration

out  = sys.argv[1] if len(sys.argv) > 1 else "pm_filter.pmc"
proc = sys.argv[2] if len(sys.argv) > 2 else "sdo_stand_alone.exe"

rules = [Rule(Column.PROCESS_NAME, RuleRelation.IS, proc, RuleAction.INCLUDE)]
with open(out, "wb") as f:
    dump_configuration({"FilterRules": rules, "DestructiveFilter": 1}, f)
print(f"wrote {out}  (include process == {proc}, drop-filtered=on)")
