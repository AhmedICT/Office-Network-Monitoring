-- CBH-NMS database schema (SRS §6). PostgreSQL 17 + TimescaleDB.
-- Applied once by the Core Platform's migrator; the whole file runs in one transaction.
-- Deviations from SRS §6.2 DDL (see docs/DECISIONS.md):
--   * region, device_type, snmp_profile, app_user defined before they are referenced.
--   * ops.alert.incident_id dropped; alert ↔ incident goes only through ops.incident_alert.
--   * Central Poller results go to mon.reachability (it has no agent_id / agent_seq).

CREATE EXTENSION IF NOT EXISTS timescaledb;
CREATE SCHEMA IF NOT EXISTS org;
CREATE SCHEMA IF NOT EXISTS inv;
CREATE SCHEMA IF NOT EXISTS mon;
CREATE SCHEMA IF NOT EXISTS ops;
CREATE SCHEMA IF NOT EXISTS sec;

-- ───────────── sec (users first: referenced by ops) ─────────────
CREATE TABLE sec.app_user (
    user_id          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ad_sid           varchar(100) UNIQUE,                   -- NULL only for the break-glass account
    user_name        varchar(100) NOT NULL UNIQUE,          -- DOMAIN\user
    display_name     varchar(150) NOT NULL,
    email            varchar(150),
    mobile           varchar(20),                           -- for SMS
    role             varchar(20)  NOT NULL CHECK (role IN ('SUPER_ADMIN','HEAD_OF_IT','IT_MANAGER','NET_ADMIN','BRANCH_ENGINEER','MANAGEMENT')),
    is_active        boolean      NOT NULL DEFAULT true,
    last_login_at    timestamptz
);

-- ───────────── org ─────────────
CREATE TABLE org.region (
    region_id        integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name             varchar(50) NOT NULL UNIQUE
);

CREATE TABLE org.branch (
    branch_id        integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    branch_code      varchar(10)  NOT NULL UNIQUE,          -- e.g. SAV01
    branch_name      varchar(100) NOT NULL,
    region_id        integer      NOT NULL REFERENCES org.region(region_id),
    address          text,
    latitude         numeric(9,6),
    longitude        numeric(9,6),
    tier             smallint     NOT NULL DEFAULT 2 CHECK (tier BETWEEN 1 AND 3),
    open_time        time,
    close_time       time,
    mpls_router_ip   inet,                                  -- Central Poller pings this
    sla_target_pct   numeric(5,2) NOT NULL DEFAULT 99.50,
    loss_threshold_pct    numeric(5,2) NOT NULL DEFAULT 5,  -- §9.1 rule 4, per-branch override
    latency_threshold_ms  integer      NOT NULL DEFAULT 300,
    is_active        boolean      NOT NULL DEFAULT true,
    created_at       timestamptz  NOT NULL DEFAULT now()
);

CREATE TABLE org.branch_contact (
    branch_contact_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    branch_id        integer      NOT NULL REFERENCES org.branch(branch_id),
    name             varchar(100) NOT NULL,
    role             varchar(50),                           -- Branch IT, Branch Manager ...
    phone            varchar(20),
    email            varchar(150),
    is_primary       boolean      NOT NULL DEFAULT false
);

CREATE TABLE org.isp (
    isp_id           integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name             varchar(100) NOT NULL UNIQUE,
    support_phone    varchar(50),
    support_email    varchar(150)
);

CREATE TABLE org.isp_link (
    isp_link_id      integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    branch_id        integer NOT NULL REFERENCES org.branch(branch_id),
    isp_id           integer NOT NULL REFERENCES org.isp(isp_id),
    link_role        varchar(10) NOT NULL CHECK (link_role IN ('MPLS','PRIMARY','SECONDARY')),
    circuit_id       varchar(50),
    bandwidth_mbps   integer NOT NULL,
    wan_interface    varchar(50),                           -- router ifName
    probe_target     inet,                                  -- external ping target for this link
    is_active        boolean NOT NULL DEFAULT true
);

-- ───────────── inv ─────────────
CREATE TABLE inv.device_type (
    device_type_id   smallint PRIMARY KEY,
    name             varchar(30) NOT NULL UNIQUE
);
INSERT INTO inv.device_type VALUES (1,'Router'),(2,'Firewall'),(3,'Switch'),(4,'Server'),(5,'UPS'),(6,'AccessPoint');

CREATE TABLE inv.snmp_profile (
    snmp_profile_id  integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name             varchar(100) NOT NULL UNIQUE,
    vendor           varchar(30),                           -- FORTINET, CISCO, MIKROTIK, APC, GENERIC
    snmp_user        varchar(100) NOT NULL,
    auth_protocol    varchar(10)  NOT NULL DEFAULT 'SHA'  CHECK (auth_protocol IN ('SHA','SHA256')),
    priv_protocol    varchar(10)  NOT NULL DEFAULT 'AES'  CHECK (priv_protocol IN ('AES','AES256')),
    auth_secret_enc  text         NOT NULL,                 -- ASP.NET Data Protection ciphertext
    priv_secret_enc  text         NOT NULL,
    oids             jsonb        NOT NULL DEFAULT '{}'     -- metric_key -> OID map (vendor profile)
);

CREATE TABLE inv.device (
    device_id        integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    branch_id        integer  NOT NULL REFERENCES org.branch(branch_id),
    device_type_id   smallint NOT NULL REFERENCES inv.device_type(device_type_id),
    vendor           varchar(30),
    host_name        varchar(100) NOT NULL,
    ip_address       inet         NOT NULL,
    mac_address      macaddr,
    model            varchar(100),
    serial_no        varchar(100),
    firmware         varchar(50),
    warranty_end     date,
    snmp_profile_id  integer REFERENCES inv.snmp_profile(snmp_profile_id),
    poll_interval_s  smallint NOT NULL DEFAULT 60,
    is_critical      boolean  NOT NULL DEFAULT false,
    is_active        boolean  NOT NULL DEFAULT true,
    UNIQUE (branch_id, ip_address)
);

CREATE TABLE inv.device_dependency (
    child_device_id  integer NOT NULL REFERENCES inv.device(device_id),
    parent_device_id integer NOT NULL REFERENCES inv.device(device_id),
    PRIMARY KEY (child_device_id, parent_device_id),
    CHECK (child_device_id <> parent_device_id)
);

CREATE TABLE inv.agent (
    agent_id         integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    branch_id        integer NOT NULL REFERENCES org.branch(branch_id),
    host_name        varchar(100) NOT NULL,
    key_hash         bytea        NOT NULL,                 -- SHA-256 of the agent key, never the key
    agent_version    varchar(20),
    config_version   integer      NOT NULL DEFAULT 0,       -- version the agent last pulled
    last_seen_at     timestamptz,
    buffer_rows      integer,
    registered_at    timestamptz  NOT NULL DEFAULT now(),
    is_revoked       boolean      NOT NULL DEFAULT false
);

CREATE TABLE inv.enroll_token (
    token_hash       bytea PRIMARY KEY,
    branch_id        integer     NOT NULL REFERENCES org.branch(branch_id),
    created_by       varchar(100) NOT NULL,
    expires_at       timestamptz NOT NULL,
    used_at          timestamptz
);

CREATE TABLE inv.app_check (
    app_check_id     integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    branch_id        integer      REFERENCES org.branch(branch_id), -- NULL = run from every branch
    name             varchar(100) NOT NULL,
    check_type       varchar(5)   NOT NULL CHECK (check_type IN ('HTTP','TCP')),
    target           varchar(300) NOT NULL,                 -- URL for HTTP, host:port for TCP
    expected_status  smallint,                              -- HTTP only; NULL = any 2xx
    timeout_ms       integer      NOT NULL DEFAULT 5000,
    business_service varchar(50),                           -- FR-AP-04 (P2)
    is_active        boolean      NOT NULL DEFAULT true
);

-- Bumped on any change that affects what agents must poll; agents compare with their copy (FR-BR-05).
CREATE TABLE inv.branch_config_version (
    branch_id        integer PRIMARY KEY REFERENCES org.branch(branch_id),
    version          integer NOT NULL DEFAULT 1,
    changed_at       timestamptz NOT NULL DEFAULT now()
);

-- ───────────── mon ─────────────
CREATE TABLE mon.metric_raw (
    time             timestamptz NOT NULL,
    entity_type      varchar(10) NOT NULL,                  -- BRANCH, DEVICE, ISPLINK, APP
    entity_id        integer     NOT NULL,
    metric_key       varchar(40) NOT NULL,
    value            double precision NOT NULL,
    agent_id         integer     NOT NULL,
    agent_seq        bigint      NOT NULL,
    UNIQUE (agent_id, agent_seq, time)
);
SELECT create_hypertable('mon.metric_raw', by_range('time', INTERVAL '1 day'));
CREATE INDEX ix_metric_entity ON mon.metric_raw (entity_type, entity_id, metric_key, time DESC);
ALTER TABLE mon.metric_raw SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'entity_type, entity_id, metric_key',
    timescaledb.compress_orderby   = 'time DESC'
);
SELECT add_compression_policy('mon.metric_raw', INTERVAL '7 days');
SELECT add_retention_policy('mon.metric_raw',   INTERVAL '30 days');

-- Central Poller ICMP results (HO → branch MPLS router).
CREATE TABLE mon.reachability (
    time             timestamptz NOT NULL,
    branch_id        integer     NOT NULL,
    success          boolean     NOT NULL,
    rtt_ms           real
);
SELECT create_hypertable('mon.reachability', by_range('time', INTERVAL '7 days'));
CREATE INDEX ix_reach_branch ON mon.reachability (branch_id, time DESC);
SELECT add_retention_policy('mon.reachability', INTERVAL '30 days');

CREATE MATERIALIZED VIEW mon.metric_hourly WITH (timescaledb.continuous) AS
SELECT time_bucket(INTERVAL '1 hour', time) AS bucket,
       entity_type, entity_id, metric_key,
       avg(value) AS avg_value, min(value) AS min_value, max(value) AS max_value, count(*) AS samples
FROM mon.metric_raw
GROUP BY bucket, entity_type, entity_id, metric_key
WITH NO DATA;
SELECT add_continuous_aggregate_policy('mon.metric_hourly',
    start_offset => INTERVAL '4 days', end_offset => INTERVAL '1 hour', schedule_interval => INTERVAL '30 minutes');
SELECT add_retention_policy('mon.metric_hourly', INTERVAL '13 months');

CREATE MATERIALIZED VIEW mon.metric_daily WITH (timescaledb.continuous) AS
SELECT time_bucket(INTERVAL '1 day', bucket) AS bucket,
       entity_type, entity_id, metric_key,
       sum(avg_value * samples) / sum(samples) AS avg_value,
       min(min_value) AS min_value, max(max_value) AS max_value, sum(samples) AS samples
FROM mon.metric_hourly
GROUP BY 1, entity_type, entity_id, metric_key
WITH NO DATA;
SELECT add_continuous_aggregate_policy('mon.metric_daily',
    start_offset => INTERVAL '5 days', end_offset => INTERVAL '1 day', schedule_interval => INTERVAL '6 hours');
SELECT add_retention_policy('mon.metric_daily', INTERVAL '5 years');

CREATE TABLE mon.status_current (
    entity_type      varchar(10) NOT NULL,
    entity_id        integer     NOT NULL,
    status           varchar(20) NOT NULL,
    since            timestamptz NOT NULL,
    detail           jsonb,
    updated_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (entity_type, entity_id)
);

CREATE TABLE mon.status_history (
    status_history_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    entity_type      varchar(10) NOT NULL,
    entity_id        integer     NOT NULL,
    status           varchar(20) NOT NULL,                  -- ONLINE, DEGRADED, OFFLINE_ISP, OFFLINE_POWER, UNKNOWN
    started_at       timestamptz NOT NULL,
    ended_at         timestamptz,
    in_maintenance   boolean     NOT NULL DEFAULT false
);
CREATE INDEX ix_status_entity ON mon.status_history (entity_type, entity_id, started_at);

CREATE TABLE mon.alert_rule (
    alert_rule_id    integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name             varchar(100) NOT NULL,
    entity_type      varchar(10)  NOT NULL,
    metric_key       varchar(40)  NOT NULL,
    operator         varchar(2)   NOT NULL CHECK (operator IN ('>','>=','<','<=','=')),
    threshold        double precision NOT NULL,
    for_seconds      integer      NOT NULL DEFAULT 0,
    severity         varchar(10)  NOT NULL CHECK (severity IN ('CRITICAL','HIGH','WARNING','INFO')),
    scope_branch_id  integer REFERENCES org.branch(branch_id),
    is_enabled       boolean      NOT NULL DEFAULT true
);

CREATE TABLE mon.maintenance_window (
    maintenance_id   integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    branch_id        integer REFERENCES org.branch(branch_id),
    device_id        integer REFERENCES inv.device(device_id),
    starts_at        timestamptz  NOT NULL,
    ends_at          timestamptz  NOT NULL,
    reason           varchar(300) NOT NULL,
    change_request_no varchar(50),
    created_by       integer NOT NULL REFERENCES sec.app_user(user_id),
    approved_by      integer REFERENCES sec.app_user(user_id), -- required when > 24 h (SCR-08)
    CHECK (ends_at > starts_at),
    CHECK (branch_id IS NOT NULL OR device_id IS NOT NULL)
);

-- ───────────── ops ─────────────
CREATE TABLE ops.alert (
    alert_id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    dedup_key        varchar(100) NOT NULL,
    alert_rule_id    integer REFERENCES mon.alert_rule(alert_rule_id),
    branch_id        integer NOT NULL REFERENCES org.branch(branch_id),
    device_id        integer REFERENCES inv.device(device_id),
    severity         varchar(10)  NOT NULL,
    title            varchar(200) NOT NULL,
    raised_at        timestamptz  NOT NULL,
    cleared_at       timestamptz,
    is_suppressed    boolean NOT NULL DEFAULT false,
    suppress_reason  varchar(30),                           -- PARENT_DOWN, MAINTENANCE, FLAPPING
    ack_by_user_id   integer REFERENCES sec.app_user(user_id),
    ack_at           timestamptz,
    ack_comment      varchar(500)
);
CREATE UNIQUE INDEX ux_alert_active ON ops.alert (dedup_key) WHERE cleared_at IS NULL;
CREATE INDEX ix_alert_branch ON ops.alert (branch_id, raised_at DESC);

CREATE TABLE ops.incident (
    incident_id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    incident_no      varchar(20)  NOT NULL UNIQUE,
    branch_id        integer      NOT NULL REFERENCES org.branch(branch_id),
    severity         varchar(10)  NOT NULL,
    status           varchar(15)  NOT NULL CHECK (status IN ('OPEN','ACK','IN_PROGRESS','RESOLVED','CLOSED')),
    title            varchar(200) NOT NULL,
    root_cause       varchar(20)  CHECK (root_cause IN ('ISP','POWER','HARDWARE','CONFIG','HIS','UNKNOWN')),
    resolution_note  text,
    assigned_user_id integer REFERENCES sec.app_user(user_id),
    escalation_level smallint NOT NULL DEFAULT 0,
    task_ticket_id   varchar(50),
    started_at       timestamptz NOT NULL,                  -- first alert raised (for MTTD)
    detected_at      timestamptz NOT NULL,
    ack_at           timestamptz,
    resolved_at      timestamptz,
    closed_at        timestamptz,
    CHECK (status NOT IN ('RESOLVED','CLOSED') OR root_cause IS NOT NULL)  -- FR-IN-04
);
CREATE SEQUENCE ops.incident_no_seq;

CREATE TABLE ops.incident_alert (
    incident_id      bigint NOT NULL REFERENCES ops.incident(incident_id),
    alert_id         bigint NOT NULL REFERENCES ops.alert(alert_id) UNIQUE,
    PRIMARY KEY (incident_id, alert_id)
);

CREATE TABLE ops.incident_timeline (
    timeline_id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    incident_id      bigint      NOT NULL REFERENCES ops.incident(incident_id),
    at               timestamptz NOT NULL DEFAULT now(),
    user_id          integer REFERENCES sec.app_user(user_id), -- NULL = system
    kind             varchar(20) NOT NULL,                  -- CREATED, STATUS, ASSIGN, NOTE, ESCALATED, NOTIFIED, TICKET
    text             text        NOT NULL
);
CREATE INDEX ix_timeline_incident ON ops.incident_timeline (incident_id, at);

CREATE TABLE ops.escalation_policy (
    escalation_policy_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    severity         varchar(10) NOT NULL,
    level            smallint    NOT NULL CHECK (level BETWEEN 1 AND 3),
    after_minutes    integer     NOT NULL,
    target_role      varchar(20) NOT NULL,
    channels         varchar(20) NOT NULL,                  -- SMS, EMAIL, SMS+EMAIL
    UNIQUE (severity, level)
);
-- §9.2 defaults
INSERT INTO ops.escalation_policy (severity, level, after_minutes, target_role, channels) VALUES
  ('CRITICAL',1,0,'BRANCH_ENGINEER','SMS+EMAIL'),('CRITICAL',2,15,'IT_MANAGER','SMS'),('CRITICAL',3,30,'HEAD_OF_IT','SMS'),
  ('HIGH',1,0,'BRANCH_ENGINEER','SMS+EMAIL'),('HIGH',2,30,'IT_MANAGER','SMS+EMAIL'),('HIGH',3,120,'HEAD_OF_IT','EMAIL');

CREATE TABLE ops.notification_rule (
    notification_rule_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    branch_id        integer REFERENCES org.branch(branch_id),  -- NULL = all
    min_severity     varchar(10) NOT NULL,
    time_window      varchar(10) NOT NULL DEFAULT 'ANY' CHECK (time_window IN ('ANY','OFFICE','NIGHT')),
    user_id          integer NOT NULL REFERENCES sec.app_user(user_id),
    channels         varchar(20) NOT NULL
);

CREATE TABLE ops.notification_log (
    notification_id  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    at               timestamptz NOT NULL DEFAULT now(),
    channel          varchar(10) NOT NULL,                  -- SMS, EMAIL
    recipient        varchar(150) NOT NULL,
    alert_id         bigint REFERENCES ops.alert(alert_id),
    incident_id      bigint REFERENCES ops.incident(incident_id),
    status           varchar(15) NOT NULL,                  -- QUEUED, SENT, DELIVERED, FAILED
    cost_bdt         numeric(8,2) NOT NULL DEFAULT 0,
    error            text
);
CREATE INDEX ix_notif_month ON ops.notification_log (at) WHERE channel = 'SMS';

-- ───────────── sec (rest) ─────────────
CREATE TABLE sec.user_branch_scope (
    scope_id         integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id          integer NOT NULL REFERENCES sec.app_user(user_id),
    region_id        integer REFERENCES org.region(region_id),
    branch_id        integer REFERENCES org.branch(branch_id),
    CHECK ((region_id IS NULL) <> (branch_id IS NULL))
);
CREATE UNIQUE INDEX ux_scope ON sec.user_branch_scope (user_id, coalesce(region_id,0), coalesce(branch_id,0));

CREATE TABLE sec.audit_log (
    audit_id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    at               timestamptz  NOT NULL DEFAULT now(),
    user_id          integer,
    user_name        varchar(100) NOT NULL,
    client_ip        inet,
    action           varchar(50)  NOT NULL,
    entity_type      varchar(30),
    entity_id        varchar(50),
    old_value        jsonb,
    new_value        jsonb
);
CREATE INDEX ix_audit_at ON sec.audit_log (at DESC);

-- ───────────── application role ─────────────
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cbhnms_app') THEN
    CREATE ROLE cbhnms_app NOLOGIN;
  END IF;
END $$;
GRANT USAGE ON SCHEMA org, inv, mon, ops, sec TO cbhnms_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA org, inv, mon, ops TO cbhnms_app;
GRANT SELECT, INSERT, UPDATE ON sec.app_user, sec.user_branch_scope TO cbhnms_app;
GRANT DELETE ON sec.user_branch_scope TO cbhnms_app;
GRANT SELECT, INSERT ON sec.audit_log TO cbhnms_app;   -- append-only (FR-US-04)
GRANT USAGE ON ALL SEQUENCES IN SCHEMA org, inv, mon, ops, sec TO cbhnms_app;
