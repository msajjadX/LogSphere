-- LogSphere reference seed (static data only; demo tenants/users are seeded by the app in Development)

INSERT INTO environments (id, name) VALUES
    (1,'Production'), (2,'Staging'), (3,'Development'), (4,'Testing')
ON CONFLICT (id) DO NOTHING;

INSERT INTO severities (id, name) VALUES
    (0,'Trace'), (1,'Debug'), (2,'Information'), (3,'Warning'), (4,'Error'), (5,'Critical')
ON CONFLICT (id) DO NOTHING;

-- Global default redaction rules (tenant_id IS NULL = platform-wide, cannot be bypassed by projects)
INSERT INTO redaction_rules (id, tenant_id, project_id, key_pattern, is_regex, strategy, applies_to, enabled)
SELECT gen_random_uuid(), NULL, NULL, p.pattern, false, p.strategy, 'All', true
FROM (VALUES
    ('password','Remove'), ('passcode','Remove'), ('pwd','Remove'), ('pin','Remove'),
    ('secret','Remove'), ('client_secret','Remove'), ('api_key','Remove'), ('apikey','Remove'),
    ('authorization','Redact'), ('access_token','Remove'), ('refresh_token','Remove'),
    ('bearer','Redact'), ('jwt','Remove'), ('cookie','Redact'), ('set-cookie','Redact'),
    ('session_id','Redact'), ('sessionid','Redact'), ('private_key','Remove'),
    ('connection_string','Remove'), ('connectionstring','Remove'),
    ('cvv','Remove'), ('cvc','Remove'), ('card_number','MaskLast4'), ('cardnumber','MaskLast4'),
    ('account_number','MaskLast4'), ('accountnumber','MaskLast4'), ('iban','MaskLast4'),
    ('biometric','Remove'), ('face_template','Remove'), ('fingerprint_template','Remove'),
    ('cnic','MaskLast4'), ('national_id','MaskLast4'), ('nationalidentity','MaskLast4'),
    ('date_of_birth','Redact'), ('dateofbirth','Redact'), ('dob','Redact'),
    ('phone','MaskLast4'), ('mobile','MaskLast4'), ('email','MaskLast4'), ('otp','Remove')
) AS p(pattern, strategy)
WHERE NOT EXISTS (SELECT 1 FROM redaction_rules r
                  WHERE r.tenant_id IS NULL AND r.project_id IS NULL AND r.key_pattern = p.pattern);

-- Global default retention policies
INSERT INTO retention_policies (id, tenant_id, project_id, environment_id, event_type, severity,
                                retention_days, archive_before_drop, legal_hold)
SELECT gen_random_uuid(), NULL, NULL, NULL, p.event_type, p.severity, p.days, false, false
FROM (VALUES
    (NULL,        0::smallint, 7),      -- Trace
    (NULL,        1::smallint, 7),      -- Debug
    (NULL,        2::smallint, 90),     -- Information (operational logs)
    ('ApiRequest',  NULL::smallint, 90),
    ('ApiResponse', NULL::smallint, 90),
    ('Performance', NULL::smallint, 90),
    ('Exception',   NULL::smallint, 180),
    ('Audit',       NULL::smallint, 2555),  -- ~7 years
    ('Security',    NULL::smallint, 365)
) AS p(event_type, severity, days)
WHERE NOT EXISTS (SELECT 1 FROM retention_policies r
                  WHERE r.tenant_id IS NULL AND r.project_id IS NULL
                    AND r.event_type IS NOT DISTINCT FROM p.event_type
                    AND r.severity   IS NOT DISTINCT FROM p.severity);
