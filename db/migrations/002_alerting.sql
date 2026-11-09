-- Alerting, incidents, notifications (SRS §4.6, §4.7, §9.2, §9.3).

-- Alerts remember which entity they are about, so the engine can clear them.
ALTER TABLE ops.alert
    ADD COLUMN entity_type varchar(10),
    ADD COLUMN entity_id   integer,
    ADD COLUMN last_value  double precision,
    ADD COLUMN last_notified_at timestamptz;
CREATE INDEX ix_alert_active ON ops.alert (cleared_at) WHERE cleared_at IS NULL;

-- Task Management sync state (INT-01): retried at 1, 5, 15 minutes.
ALTER TABLE ops.incident
    ADD COLUMN ticket_attempts       smallint NOT NULL DEFAULT 0,
    ADD COLUMN ticket_next_attempt_at timestamptz,
    ADD COLUMN ticket_status         varchar(30);

-- notification_log doubles as the outbox: rows are QUEUED, then a sender delivers and retries them.
ALTER TABLE ops.notification_log
    ADD COLUMN user_id         integer REFERENCES sec.app_user(user_id),
    ADD COLUMN subject         varchar(200),
    ADD COLUMN body            text,
    ADD COLUMN attempts        smallint NOT NULL DEFAULT 0,
    ADD COLUMN next_attempt_at timestamptz,
    ADD COLUMN sent_at         timestamptz,
    ADD COLUMN is_digest       boolean NOT NULL DEFAULT false;
CREATE INDEX ix_notif_queue ON ops.notification_log (next_attempt_at) WHERE status = 'QUEUED';

ALTER TABLE mon.maintenance_window ADD COLUMN created_at timestamptz NOT NULL DEFAULT now();

-- One-shot flags such as "80% SMS budget warning sent for 2026-11".
CREATE TABLE ops.system_flag (
    flag_key   varchar(100) PRIMARY KEY,
    set_at     timestamptz NOT NULL DEFAULT now()
);

-- Default rules (from the UI prototype / §9.1–9.2). All editable in SCR-09.
INSERT INTO mon.alert_rule (name, entity_type, metric_key, operator, threshold, for_seconds, severity) VALUES
  ('ISP link down',          'ISPLINK', 'up',              '=',  0,   180, 'HIGH'),
  ('High packet loss',       'BRANCH',  'loss_pct',        '>',  5,   600, 'WARNING'),
  ('High latency',           'BRANCH',  'latency_ms',      '>',  300, 600, 'WARNING'),
  ('UPS on battery',         'DEVICE',  'ups_on_battery',  '=',  1,   0,   'HIGH'),
  ('UPS battery low',        'DEVICE',  'ups_battery_pct', '<',  30,  0,   'CRITICAL'),
  ('HIS unreachable',        'APP',     'up',              '=',  0,   120, 'CRITICAL'),
  ('Device unreachable',     'DEVICE',  'up',              '=',  0,   180, 'HIGH'),
  ('Device CPU high',        'DEVICE',  'cpu_pct',         '>',  90,  600, 'WARNING'),
  ('Disk almost full',       'DEVICE',  'disk_used_pct',   '>',  85,  0,   'WARNING');

GRANT SELECT, INSERT, UPDATE, DELETE ON ops.system_flag TO cbhnms_app;
