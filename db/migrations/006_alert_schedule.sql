-- 006: alert rule schedules
-- Rules can be limited to active hours and days (evaluated in the rule's time zone), so
-- absence alerts don't fire during expected quiet periods (nights, Sundays) and volume
-- thresholds can differ per period via separate scheduled rules.
ALTER TABLE alert_rules ADD COLUMN IF NOT EXISTS active_from smallint;  -- minute of day 0-1439
ALTER TABLE alert_rules ADD COLUMN IF NOT EXISTS active_to   smallint;  -- exclusive; from>to wraps midnight
ALTER TABLE alert_rules ADD COLUMN IF NOT EXISTS active_days smallint[]; -- 0=Sunday .. 6=Saturday; NULL = all
ALTER TABLE alert_rules ADD COLUMN IF NOT EXISTS time_zone   text;       -- IANA id, e.g. Asia/Karachi; NULL = UTC
