-- prd-insights-dashboard.md rules 6-7: the word total behind the dashboard's headline card.
-- Nullable, not `not null default 0`: every row that exists today predates this column and has
-- no count. A default of 0 would make those rows claim, permanently and silently, that they
-- contained no words — a wrong number that looks exactly like a right one. NULL means "not
-- counted yet", is excluded from sums, and is filled in by backfillWordCounts at boot.
alter table dictations add column word_count integer;
