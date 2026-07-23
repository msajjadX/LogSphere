-- Triage activity trail for exception groups (who changed status/assignee/notes and when)

CREATE TABLE IF NOT EXISTS exception_group_activity (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    group_id    uuid NOT NULL REFERENCES exception_groups(id) ON DELETE CASCADE,
    user_id     uuid,
    username    text,
    action      text NOT NULL,           -- StatusChanged, Assigned, Unassigned, NotesUpdated, IssueLinked
    details     jsonb,
    occurred_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_exgroup_activity ON exception_group_activity(group_id, id DESC);
