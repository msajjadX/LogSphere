-- 005: fine-grained absence alerts (dead-man switches)
-- IngestSilence rules can now watch a specific module and/or action instead of a whole
-- project/environment scope, and trigger when fewer than an expected number of events
-- arrived (min_count) rather than only on total silence.
ALTER TABLE alert_rules ADD COLUMN IF NOT EXISTS module_filter text;
ALTER TABLE alert_rules ADD COLUMN IF NOT EXISTS action_filter text;
ALTER TABLE alert_rules ADD COLUMN IF NOT EXISTS min_count int NOT NULL DEFAULT 0;
