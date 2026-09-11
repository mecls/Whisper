-- prd-sub-second-dictation.md rule 15/16: release→paste latency, measured on the client.
-- Nullable on purpose: rows written by older clients, and every row already in the table,
-- legitimately have no measurement and must not be confused with a zero.
alter table dictations add column total_ms integer;
