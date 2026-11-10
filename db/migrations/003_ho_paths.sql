-- CBH-NMS 003: two HO↔branch paths — MPLS and site-to-site VPN (see docs/DECISIONS.md D-13).
-- The Central Poller pings the branch end of each configured path; the branch is cut off from HO
-- only when every configured path is down.

ALTER TABLE org.branch ADD COLUMN vpn_router_ip inet;      -- branch end of the VPN tunnel (firewall/router)
COMMENT ON COLUMN org.branch.mpls_router_ip IS 'Branch router on the MPLS network; pinged by the Central Poller';
COMMENT ON COLUMN org.branch.vpn_router_ip  IS 'Branch end of the site-to-site VPN tunnel; pinged by the Central Poller';

-- Existing rows all came from MPLS pings.
ALTER TABLE mon.reachability
    ADD COLUMN path varchar(4) NOT NULL DEFAULT 'MPLS' CHECK (path IN ('MPLS', 'VPN'));
DROP INDEX IF EXISTS mon.ix_reach_branch;
CREATE INDEX ix_reach_branch_path ON mon.reachability (branch_id, path, time DESC);
