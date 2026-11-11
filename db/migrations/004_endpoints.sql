-- CBH-NMS 004: User PC monitoring (Endpoint Agent) — see docs/DECISIONS.md D-14.
-- Health, hardware/software inventory, network and (only when enabled by policy) application usage.
-- Never collected: keystrokes, screenshots, window titles, URLs, file contents.

CREATE SCHEMA IF NOT EXISTS ep;
GRANT USAGE ON SCHEMA ep TO cbhnms_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA ep GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO cbhnms_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA ep GRANT USAGE, SELECT ON SEQUENCES TO cbhnms_app;

-- Reusable enrollment keys (one per branch / rollout wave), so hundreds of PCs can be installed by GPO/SCCM.
CREATE TABLE ep.enroll_key (
    enroll_key_id    integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    branch_id        integer      NOT NULL REFERENCES org.branch(branch_id),
    label            varchar(100),
    key_hash         bytea        NOT NULL UNIQUE,
    key_hint         varchar(8)   NOT NULL,                  -- last characters, to recognise a key in the UI
    expires_at       timestamptz  NOT NULL,
    max_uses         integer,                                -- null = unlimited until expiry
    uses             integer      NOT NULL DEFAULT 0,
    is_revoked       boolean      NOT NULL DEFAULT false,
    created_by       varchar(100),
    created_at       timestamptz  NOT NULL DEFAULT now()
);

CREATE TABLE ep.endpoint (
    endpoint_id      integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    branch_id        integer      NOT NULL REFERENCES org.branch(branch_id),
    machine_guid     varchar(64)  NOT NULL UNIQUE,           -- re-installing keeps the same row
    host_name        varchar(100) NOT NULL,
    key_hash         bytea        NOT NULL,
    agent_version    varchar(20),
    domain           varchar(100),
    os_name          varchar(100),
    os_version       varchar(50),
    os_build         varchar(20),
    manufacturer     varchar(100),
    model            varchar(100),
    serial_no        varchar(100),
    cpu_model        varchar(150),
    cpu_cores        smallint,
    ram_mb           integer,
    last_boot_at     timestamptz,
    ip_address       inet,
    mac_address      varchar(20),
    last_user        varchar(100),
    security         jsonb,                                  -- antivirus, firewall, updates, pending reboot
    nics             jsonb,
    live             jsonb,                                  -- newest health/network snapshot (disks, top processes)
    last_seen_at     timestamptz,
    inventory_at     timestamptz,
    registered_at    timestamptz  NOT NULL DEFAULT now(),
    is_revoked       boolean      NOT NULL DEFAULT false
);
CREATE INDEX ix_endpoint_branch ON ep.endpoint (branch_id);

-- Per-minute health and network samples.
CREATE TABLE ep.metric (
    time             timestamptz      NOT NULL,
    endpoint_id      integer          NOT NULL,
    metric_key       varchar(40)      NOT NULL,
    value            double precision NOT NULL,
    UNIQUE (endpoint_id, metric_key, time)
);
SELECT create_hypertable('ep.metric', by_range('time', INTERVAL '7 days'));
ALTER TABLE ep.metric SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'endpoint_id, metric_key',
    timescaledb.compress_orderby   = 'time DESC'
);
SELECT add_compression_policy('ep.metric', INTERVAL '7 days');
SELECT add_retention_policy('ep.metric', INTERVAL '30 days');

CREATE TABLE ep.software (
    endpoint_id      integer      NOT NULL REFERENCES ep.endpoint(endpoint_id) ON DELETE CASCADE,
    name             varchar(300) NOT NULL,
    version          varchar(100) NOT NULL DEFAULT '',
    publisher        varchar(200),
    install_date     date,
    first_seen       timestamptz  NOT NULL DEFAULT now(),
    last_seen        timestamptz  NOT NULL DEFAULT now(),
    PRIMARY KEY (endpoint_id, name, version)
);
CREATE INDEX ix_software_name ON ep.software (lower(name));

CREATE TABLE ep.software_event (
    event_id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    time             timestamptz  NOT NULL DEFAULT now(),
    endpoint_id      integer      NOT NULL REFERENCES ep.endpoint(endpoint_id) ON DELETE CASCADE,
    action           varchar(10)  NOT NULL CHECK (action IN ('INSTALLED', 'REMOVED', 'UPDATED')),
    name             varchar(300) NOT NULL,
    version          varchar(100),
    old_version      varchar(100)
);
CREATE INDEX ix_software_event ON ep.software_event (endpoint_id, time DESC);

-- BLOCKED: alert when installed. REQUIRED: alert when missing (e.g. the antivirus agent).
CREATE TABLE ep.software_rule (
    rule_id          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind             varchar(10)  NOT NULL CHECK (kind IN ('BLOCKED', 'REQUIRED')),
    pattern          varchar(200) NOT NULL,                  -- wildcard, e.g. "*torrent*"
    like_pattern     varchar(400) NOT NULL,                  -- same, as an escaped ILIKE pattern
    severity         varchar(10)  NOT NULL DEFAULT 'WARNING' CHECK (severity IN ('CRITICAL', 'HIGH', 'WARNING', 'INFO')),
    note             varchar(300),
    is_enabled       boolean      NOT NULL DEFAULT true,
    created_at       timestamptz  NOT NULL DEFAULT now()
);

-- Application usage: seconds of foreground use per app (exe) per user per day. No window titles or URLs.
CREATE TABLE ep.app_usage (
    endpoint_id      integer      NOT NULL REFERENCES ep.endpoint(endpoint_id) ON DELETE CASCADE,
    day              date         NOT NULL,
    user_name        varchar(100) NOT NULL,
    app              varchar(260) NOT NULL,
    product          varchar(200),
    active_s         integer      NOT NULL DEFAULT 0,
    PRIMARY KEY (endpoint_id, day, user_name, app)
);

CREATE TABLE ep.session_event (
    time             timestamptz  NOT NULL,
    endpoint_id      integer      NOT NULL REFERENCES ep.endpoint(endpoint_id) ON DELETE CASCADE,
    user_name        varchar(100) NOT NULL,
    event            varchar(10)  NOT NULL CHECK (event IN ('LOGON', 'LOGOFF', 'LOCK', 'UNLOCK', 'IDLE', 'ACTIVE'))
);
CREATE INDEX ix_session_event ON ep.session_event (endpoint_id, time DESC);

-- Additive uploads (app usage) must not double-count when the agent re-sends after a lost response.
CREATE TABLE ep.ingest_batch (
    endpoint_id      integer      NOT NULL,
    batch_id         uuid         NOT NULL,
    received_at      timestamptz  NOT NULL DEFAULT now(),
    PRIMARY KEY (endpoint_id, batch_id)
);

CREATE TABLE ep.setting (
    key              varchar(50)  PRIMARY KEY,
    value            jsonb        NOT NULL,
    updated_by       varchar(100),
    updated_at       timestamptz  NOT NULL DEFAULT now()
);
-- Activity tracking is OFF until an administrator enables it after the staff notice is published.
INSERT INTO ep.setting (key, value) VALUES
    ('activity', '{"enabled": false, "retentionDays": 90}'),
    ('alerts',   '{"diskFreePct": 10}');

GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA ep TO cbhnms_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA ep TO cbhnms_app;
